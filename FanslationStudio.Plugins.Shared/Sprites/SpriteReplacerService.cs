using FanslationStudio.Plugins.Shared;
using FanslationStudio.Plugins.Support;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.UI;

namespace FanslationStudio.Plugins.Sprites;

public class SpriteReplacerService
{
    internal static IPluginLogger _logger;
    public bool _enabled = false;

    public static bool ContractsLoaded = false;
    public static Dictionary<string, SpriteReplacerContract> Contracts = [];
    public static Dictionary<string, SpriteReplacerContract> CachedMatchesContracts = [];
    private static string _folder;

    private static IYamlHelper _yamlHelper;

    // Host-runtime-specific finder for Image components. See ISpriteElementFinder for why this
    // can't be a direct FindObjectsOfType<T>() call from Shared.
    private static ISpriteElementFinder _elementFinder;

    public SpriteReplacerService(IPluginLogger logger, bool enabled, string bepinexRootPath, IYamlHelper yamlHelper, ISpriteElementFinder elementFinder)
    {
        _logger = logger;
        _enabled = enabled;
        _yamlHelper = yamlHelper;
        _elementFinder = elementFinder;
        _folder = Path.Combine(bepinexRootPath, "sprites2");
    }

    // Tracks whether Harmony patching has been applied yet. Patching is deferred (see
    // EnsurePatched) rather than done immediately in Awake/Load, because under IL2CPP,
    // Harmony resolves Il2CppType tokens for patch parameter types (e.g. Image, GameObject)
    // via Il2CppType.From. If this runs before Unity has naturally initialized those modules,
    // it can force their static cctor to run reentrantly inside Il2CppInterop's generic-method
    // hook, corrupting memory (AccessViolationException) and crashing the game.
    private static bool _patched = false;

    public void Awake()
    {
        if (!_enabled)
            return;

        if (!Directory.Exists(_folder))
            Directory.CreateDirectory(_folder);

        LoadContracts();

    }

    /// <summary>
    /// Applies the Harmony patches. Must be called after at least one frame/scene has run
    /// (e.g. from the plugin's Update, not from Awake/Load) so Unity has had a chance to
    /// naturally initialize the relevant modules before Harmony/Il2CppInterop tries to
    /// resolve their type tokens - doing this too early can crash the game under IL2CPP.
    /// </summary>
    public void EnsurePatched()
    {
        if (_patched || !_enabled)
            return;

        Harmony.CreateAndPatchAll(typeof(SpriteReplacerService));
        _patched = true;
        _logger.LogWarning($"SpriteReplacerV2 Plugin patched!");
    }

    public void Reload()
    {

        ApplyAllContracts();
        _logger.LogWarning("Sprite Contracts Reloaded");
    }

    public void AddAtCursor(float x, float y, float z)
    {
        _logger.LogWarning("Adding Sprite Contracts at Cursor");
        AddElementsToContracts(FindElementsAtCursor(x, y, z));
    }

    public void AddAll()
    {
        _logger.LogWarning("Adding Sprite Contracts on Scene");
        AddElementsToContracts(SpriteReplacerService.FindAllElements());
    }

    public static void ApplyAllContracts()
    {
        foreach (var element in FindAllElements())
            ReplaceSpriteInAsset(element);
    }

    public void LoadContracts()
    {
        ContractsLoaded = false;

        Contracts.Clear();
        CachedMatchesContracts.Clear();

        var contractFiles = Directory.EnumerateFiles(_folder, "*.yaml");
        foreach (var file in contractFiles)
        {
            try
            {
                var content = File.ReadAllText(file);
                if (string.IsNullOrWhiteSpace(content))
                    continue;

                var newContracts = _yamlHelper.Deserialize<List<SpriteReplacerContract>>(content);
                AddFoundContracts(newContracts);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error Loading sprite contract '{file}': {ex}");
            }
        }

        ContractsLoaded = true;
    }

    private void AddFoundContracts(List<SpriteReplacerContract> newContracts)
    {
        foreach (var newContract in newContracts)
        {
            if (!Contracts.ContainsKey(newContract.Path))
            {
                Contracts.Add(newContract.Path, newContract);
                if (CachedMatchesContracts.ContainsKey(newContract.Path))
                    CachedMatchesContracts[newContract.Path] = newContract;
            }
        }
    }

    public static Image[] FindAllElements()
    {
        return _elementFinder.FindAllElements();
    }

    public Image[] FindElementsAtCursor(float x, float y, float z)
    {
        // Create a 10x10 pixel area around the cursor (20 pixel buffer on each side)
        var cursorArea = new Rect(x - 10, y - 10, 20, 20);

        // Find all elements in the scene. Delegates to the host-specific finder rather than
        // calling FindObjectsOfType<T>() directly - see ISpriteElementFinder.
        var elements = _elementFinder.FindAllElements();

        var responseElements = new List<Image>();

        foreach (var element in elements)
        {
            // Get the RectTransform to check if it contains the cursor position
            var rectTransform = element.rectTransform;
            if (rectTransform == null)
                continue;

            // Check if the text element's screen rect overlaps with our cursor area
            var canvas = element.canvas;
            if (canvas == null)
                continue;

            // Get the screen rect of the text element
            var camera = canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
            var screenRect = RectTransformUtility.PixelAdjustRect(rectTransform, canvas);

            // Convert the rect to screen coordinates if not in overlay mode
            if (canvas.renderMode != RenderMode.ScreenSpaceOverlay && camera != null)
            {
                var corners = new Vector3[4];
                rectTransform.GetWorldCorners(corners);

                // Convert world corners to screen points
                var min = camera.WorldToScreenPoint(corners[0]);
                var max = camera.WorldToScreenPoint(corners[2]);
                screenRect = new Rect(min.x, min.y, max.x - min.x, max.y - min.y);
            }

            // Check if the cursor area overlaps with the text element's screen rect
            if (screenRect.Overlaps(cursorArea))
                responseElements.Add(element);
        }

        return responseElements.ToArray();
    }

    public void AddElementsToContracts(Image[] elements, bool addUnderCursor = false, bool copyUnderCursor = false)
    {
        var foundContracts = new List<SpriteReplacerContract>();

        foreach (var element in elements)
        {
            //Logger.LogWarning($"Found Image Element: {element.name}");

            if (element.sprite == null)
                continue;

            //Logger.LogWarning($"Found Image Sprite: {element.sprite.name}");

            // Log information about the text element
            var path = ObjectHelper.GetGameObjectPath(element.gameObject);
            var spriteName = element.sprite.name;

            if (!Contracts.ContainsKey(path))
            {
                // Create a new resizer contract for this text element
                var newContract = new SpriteReplacerContract()
                {
                    Path = path,
                    ReplacementSprite = CalculateReplacement(spriteName, path),
                };

                var spritePath = $"{_folder}/dumped/{newContract.ReplacementSprite}";
                //Logger.LogWarning($"Found Sprite Path: {spritePath}");

                if (!File.Exists(spritePath))
                {
                    var texture = element.sprite.texture;
                    byte[] bytes;

                    if (texture.isReadable)
                    {
                        bytes = texture.GetRawTextureData();
                    }
                    else
                    {
                        // Create a temporary readable texture
                        var readableTexture = new Texture2D(texture.width, texture.height, texture.format, false);
                        var renderTexture = RenderTexture.GetTemporary(texture.width, texture.height);

                        Graphics.Blit(texture, renderTexture);
                        RenderTexture.active = renderTexture;
                        readableTexture.ReadPixels(new Rect(0, 0, renderTexture.width, renderTexture.height), 0, 0);
                        readableTexture.Apply();

                        RenderTexture.active = null;
                        RenderTexture.ReleaseTemporary(renderTexture);

                        bytes = readableTexture.EncodeToPNG();
                        UnityEngine.Object.Destroy(readableTexture);
                    }

                    if (bytes == null || bytes.Length == 0)
                    {
                        //Logger.LogError($"Empty or null sprite data for: {newContract.ReplacementSprite}");
                        continue;
                    }

                    File.WriteAllBytes($"{spritePath}.png", bytes);

                }

                foundContracts.Add(newContract);
            }
        }

        if (foundContracts.Count > 0)
        {            
            var addedContractsFile = $"{_folder}/zzAdded.yaml";
            var newText = _yamlHelper.Serialize(foundContracts);

            _logger.LogWarning($"Writing to {addedContractsFile}");

            if (!File.Exists(addedContractsFile))
                File.WriteAllText(addedContractsFile, newText);
            else
                File.AppendAllText(addedContractsFile, newText);

            AddFoundContracts(foundContracts);
        }
        else
            _logger.LogMessage("No new sprite elements found in scene");
    }

    public string CalculateReplacement(string spriteName, string objectPath)
    {
        if (Contracts.Any(c => c.Value.ReplacementSprite == spriteName))
        {
            var segments = objectPath.Split('/');
            var prefix = string.Empty;
            if (segments.Length < 3)
                prefix = objectPath;
            else
                prefix = string.Join("/", segments.Skip(segments.Length - 3));

            prefix = prefix
                .Replace(" ", "_")
                .Replace("(", "")
                .Replace(")", "")
                .Replace(":", "")
                .Replace("/", "_")
                .Replace("\\", "_"); ;

            spriteName = $"{prefix}_{spriteName}";
        }

        return spriteName;
    }

    public static SpriteReplacerContract FindAppropriateContract(string path)
    {
        if (Contracts.TryGetValue(path, out var tryContract))
            return tryContract;

        // Check cache first
        if (CachedMatchesContracts.TryGetValue(path, out var cachedContract))
            return cachedContract;

        // Try wildcard matching for the remaining resizers
        foreach (var contractPair in Contracts)
        {
            var contract = contractPair.Value;

            if (contract.Path.Contains("*"))
            {
                // Convert to Regex
                var pattern = contract.Path
                    .Replace("/", @"\/")
                    .Replace("(", @"\(")
                    .Replace(")", @"\)")
                    .Replace("*", ".*");

                if (Regex.IsMatch(path, pattern))
                    return contract;
            }
        }

        return null;
    }

    public static void ReplaceSpriteInAsset(Image image)
    {
        if (image == null)
            return;

        if (image.sprite == null)
            return;

        //Logger.LogWarning($"Checking Image: {child.name}");

        var path = image.GetObjectPath();
        var contract = FindAppropriateContract(path);

        //Logger.LogWarning($"Checking Image Path: {path} Contract: {contract}");

        // Cache matches so we only have to match once
        // We cache nulls so we don't loop multiple times over the same path
        if (!CachedMatchesContracts.ContainsKey(path))
            CachedMatchesContracts.Add(path, contract);

        if (contract == null)
            return;

        // Replace the sprite
        var spriteReplacementPath = $"{_folder}/dumped/{contract.ReplacementSprite}.png";

        if (!File.Exists(spriteReplacementPath))
        {
            _logger.LogError($"Sprite not found at path: {spriteReplacementPath}");
            return;
        }

        try
        {
            var bytes = File.ReadAllBytes(spriteReplacementPath);
            var originalTexture = image.sprite.texture;

            // We use an uncompressed format to avoid issues with compression requiring specific sizes (eg. DXT1, DXT5, BC7, BC6H)
            // Texture size doesn't matter, will be replaced by Unity in LoadImage to match texture
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            texture.LoadImage(bytes);

            // Ensure preserve aspect is on to avoid stretching issues where replacement sprite dimensions differ
            image.preserveAspect = true;

            // Ensure the rect fits within the new texture dimensions 
            var rect = image.sprite.rect;
            if (rect.width > texture.width || rect.height > texture.height)
            {
                _logger.LogWarning($"{spriteReplacementPath}: Texture dimensions are smaller than sprite rect, resizing");
                rect.width = Mathf.Min(rect.width, texture.width);
                rect.height = Mathf.Min(rect.height, texture.height);
            }

            image.sprite = Sprite.Create(texture, rect, image.sprite.pivot, image.sprite.pixelsPerUnit); ;
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error replacing sprite: {ex}");
        }
    }

    //[HarmonyPrefix, HarmonyPatch(typeof(SweetPotato.ResourceManager), "GetAssetObjectSprite")]
    //static bool Prefix(string path, ref Sprite __result)
    //{
    //    if (CheckPath)


    //    // Load your custom sprite
    //    string modAssetPath = Path.Combine(Paths.PluginPath, "MyModAssets", path.Replace("MyMod/", "") + ".png");
    //    if (File.Exists(modAssetPath))
    //    {
    //        byte[] fileData = File.ReadAllBytes(modAssetPath);
    //        Texture2D texture = new Texture2D(2, 2);
    //        texture.LoadImage(fileData);
    //        __result = Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), new Vector2(0.5f, 0.5f));
    //        return false; // Skip the original method
    //    }

    //    // Fall back to the original method
    //    return true;
    //}
}
