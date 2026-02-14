using FanslationStudio.Plugins.Support;
using HarmonyLib;
using HarmonyLib.Tools;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using TMPro;
using UnityEngine;

namespace FanslationStudio.Plugins.TextResizer;

public class TextResizerService
{
    internal static IPluginLogger _logger;
    public static bool _enabled = false;
    private string _resizerFolder;

    // Required Static for patches to see it
    public static bool ResizersLoaded = false;
    public static Dictionary<string, TextResizerContract> Resizers = [];

    // Cache for storing previously matched results
    public static Dictionary<string, TextResizerContract> CachedMatchedResizers = [];


    public TextResizerService(IPluginLogger logger, bool enabled, string bepinexRootPath)
    {
        _logger = logger;
        _enabled = enabled;
        _resizerFolder = Path.Combine(bepinexRootPath, "sprites2");
    }

    public void Awake()
    {
        if (!_enabled)
            return; 

        Harmony.CreateAndPatchAll(typeof(TextResizerService));
        _logger.LogWarning($"TextResizer Plugin should be patched!");

        if (!Directory.Exists(_resizerFolder))
            Directory.CreateDirectory(_resizerFolder);

        LoadResizers();
        _logger.LogWarning($"TextResizer Plugin Loaded!");
    }


    public void Reload()
    {
        LoadResizers();
        ApplyAllResizers();
        _logger.LogWarning("Resizers Reloaded");
    }

    public void AddResizersForScene()
    {
        _logger.LogWarning("Adding Resizers for Scene");
        AddTextElementsToResizers(FindAllTextElements());
    }

    public void AddResizersAtCursor(float x, float y, float z)
    {
        _logger.LogWarning("Adding Resizers at Cursor");
        AddTextElementsToResizers(FindTextElementsUnderCursor(x, y, z), addUnderCursor: true);
    }    

    public void LoadResizers()
    {
        ResizersLoaded = false;

        var deserializer = Yaml.CreateDeserializer();
        Resizers.Clear();
        CachedMatchedResizers.Clear();

        var resizerFiles = Directory.EnumerateFiles(_resizerFolder, "*.yaml");
        foreach (var file in resizerFiles)
        {
            try
            {
                var content = File.ReadAllText(file);
                if (string.IsNullOrWhiteSpace(content))
                    continue;

                var newResizers = deserializer.Deserialize<List<TextResizerContract>>(content);
                AddFoundResizers(newResizers);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error Loading resizer '{file}': {ex}");
            }
        }

        ResizersLoaded = true;
    }

    private void AddFoundResizers(List<TextResizerContract> newResizers)
    {
        foreach (var newResizer in newResizers)
            if (!Resizers.ContainsKey(newResizer.Path))
                Resizers.Add(newResizer.Path, newResizer);
    }

    public TextMeshProUGUI[] FindTextElementsUnderCursor(float x, float y, float z)
    {
        // Create a 10x10 pixel area around the cursor (20 pixel buffer on each side)
        var cursorArea = new Rect(x - 10, y - 10, 20, 20);

        // Find all TextMeshProUGUI components in the scene
        var textElements = UnityEngine.Object.FindObjectsOfType<TextMeshProUGUI>();

        var responseElements = new List<TextMeshProUGUI>();

        foreach (TextMeshProUGUI textElement in textElements)
        {
            // Get the RectTransform to check if it contains the cursor position
            var rectTransform = textElement.rectTransform;
            if (rectTransform == null) continue;

            // Check if the text element's screen rect overlaps with our cursor area
            Canvas canvas = textElement.canvas;
            if (canvas == null) continue;

            // Get the screen rect of the text element
            Camera camera = canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
            Rect screenRect = RectTransformUtility.PixelAdjustRect(rectTransform, canvas);

            // Convert the rect to screen coordinates if not in overlay mode
            if (canvas.renderMode != RenderMode.ScreenSpaceOverlay && camera != null)
            {
                Vector3[] corners = new Vector3[4];
                rectTransform.GetWorldCorners(corners);

                // Convert world corners to screen points
                Vector2 min = camera.WorldToScreenPoint(corners[0]);
                Vector2 max = camera.WorldToScreenPoint(corners[2]);
                screenRect = new Rect(min.x, min.y, max.x - min.x, max.y - min.y);
            }

            // Check if the cursor area overlaps with the text element's screen rect
            if (screenRect.Overlaps(cursorArea))
                responseElements.Add(textElement);
        }

        return responseElements.ToArray();
    }

    public static TextMeshProUGUI[] FindAllTextElements()
    {
        // Find all TextMeshProUGUI components in the scene
        return UnityEngine.Object.FindObjectsOfType<TextMeshProUGUI>();
    }

    public void AddTextElementsToResizers(TextMeshProUGUI[] textElements, bool addUnderCursor = false, bool copyUnderCursor = false)
    {
        var foundResizers = new List<TextResizerContract>();

        foreach (TextMeshProUGUI textElement in textElements)
        {
            // Log information about the text element
            var path = ObjectHelper.GetGameObjectPath(textElement.gameObject);
            //Logger.LogMessage($"Found text element: {path}");

            if (!Resizers.ContainsKey(path))
            {
                // Create a new resizer contract for this text element
                var newResizer = new TextResizerContract()
                {
                    Path = path,
                    SampleText = textElement.text,
                    IdealFontSize = textElement.fontSize,
                    //AllowAutoSizing = textElement.enableAutoSizing,
                    AllowWordWrap = textElement.enableWordWrapping,
                    //Alignment = textElement.alignment.ToString(),
                    //OverflowMode = textElement.overflowMode.ToString(),
                    //Add More if we want more
                    AllowLeftTrimText = false, //Want to serialise
                };

                foundResizers.Add(newResizer);
            }
        }

        if (foundResizers.Count > 0)
        {
            var serializer = Yaml.CreateSerializer();

            var addedResizersFile = $"{_resizerFolder}/zzAddedResizers.yaml";
            var newText = serializer.Serialize(foundResizers);

            _logger.LogWarning($"Writing to {addedResizersFile}");

            if (!File.Exists(addedResizersFile))
                File.WriteAllText(addedResizersFile, newText);
            else
                File.AppendAllText(addedResizersFile, newText);

            AddFoundResizers(foundResizers);
        }
        else
        {
            _logger.LogMessage("No new text elements found in scene");
        }
    }

    public static void ApplyResizing(TextMeshProUGUI textComponent)
    {
        if (textComponent == null)
            return;

        if (textComponent.gameObject == null)
            return;

        try
        {
            textComponent.wordWrappingRatios = 1.0f; //Disable Word wrapping ratios (should stop eastern rules)
            textComponent.enableKerning = false;

            var path = ObjectHelper.GetGameObjectPath(textComponent.gameObject);
            var resizer = FindAppropriateResizer(path);

            // Cache the wildcard match so we only have to match once
            if (!CachedMatchedResizers.ContainsKey(path))
                CachedMatchedResizers.Add(path, resizer);

            if (resizer == null)
                return;

            // Cache components
            var rectTransform = textComponent.rectTransform;
            var metadata = textComponent.GetComponent<TextMetadata>();

            // If metadata is not attached, add it and store the original values against it
            if (metadata == null)
            {
                metadata = textComponent.gameObject.AddComponent<TextMetadata>();
                metadata.OriginalX = rectTransform.anchoredPosition.x;
                metadata.OriginalY = rectTransform.anchoredPosition.y;
                metadata.OriginalWidth = rectTransform.sizeDelta.x;
                metadata.OriginalHeight = rectTransform.sizeDelta.y;
                metadata.OriginalCharacterSpacing = textComponent.characterSpacing;
                metadata.OriginalLineSpacing = textComponent.lineSpacing;
                metadata.OriginalWordSpacing = textComponent.wordSpacing;
                metadata.OriginalAlignment = textComponent.alignment;
                metadata.OriginalOverflowMode = textComponent.overflowMode;
                metadata.OriginalAllowWordWrap = textComponent.enableWordWrapping;
                metadata.OriginalAllowAutoSizing = textComponent.enableAutoSizing;
                metadata.OriginalFontSize = textComponent.fontSize;
            }

            // Set this so we can debug bad resizers
            metadata.ActiveResizerPath = resizer.Path;

            // Apply position change if needed
            if (resizer.AdjustX != metadata.AdjustX
                || resizer.AdjustY != metadata.AdjustY)
            {
                metadata.AdjustX = resizer.AdjustX;
                metadata.AdjustY = resizer.AdjustY;
                rectTransform.anchoredPosition = new Vector2(metadata.OriginalX + resizer.AdjustX, metadata.OriginalY + resizer.AdjustY);
            }

            // Apply size change if needed
            if (resizer.AdjustWidth != metadata.AdjustWidth
                || resizer.AdjustHeight != metadata.AdjustHeight)
            {
                metadata.AdjustWidth = resizer.AdjustWidth;
                metadata.AdjustHeight = resizer.AdjustHeight;
                rectTransform.sizeDelta = new Vector2(metadata.OriginalWidth + metadata.AdjustWidth, metadata.OriginalHeight + metadata.AdjustHeight);
            }

            // Apply the resizing
            if (textComponent.fontSize != resizer.IdealFontSize
                && resizer.IdealFontSize != null)
            {
                textComponent.fontSize = resizer.IdealFontSize.Value;
            }
            else if (resizer.FontPercentage != null)
            {
                textComponent.fontSize = metadata.OriginalFontSize * resizer.FontPercentage ?? 1;
            }

            // Text Alignment
            var validAlignment = Enum.TryParse<TextAlignmentOptions>(resizer.Alignment, true, out var alignment);
            if (resizer.Alignment != string.Empty && !validAlignment)
                _logger.LogWarning($"Invalid alignment value: {resizer.Alignment} on {resizer.Path}");

            if (validAlignment && textComponent.alignment != alignment)
            {
                textComponent.alignment = alignment;
            }
            else if (!validAlignment && textComponent.alignment != metadata.OriginalAlignment)
            {
                textComponent.alignment = metadata.OriginalAlignment;
            }

            var validOverflow = Enum.TryParse<TextOverflowModes>(resizer.OverflowMode, true, out var overflowMode);
            if (resizer.OverflowMode != string.Empty && !validOverflow)
                _logger.LogWarning($"Invalid overflow value: {resizer.OverflowMode} on {resizer.Path}");

            if (validOverflow && textComponent.overflowMode != overflowMode)
            {
                textComponent.overflowMode = overflowMode;
            }
            else if (!validOverflow && textComponent.overflowMode != metadata.OriginalOverflowMode)
            {
                textComponent.overflowMode = metadata.OriginalOverflowMode;
            }

            // Toggles
            if (resizer.AllowWordWrap.HasValue
                && textComponent.enableWordWrapping != resizer.AllowWordWrap.Value)
            {
                textComponent.enableWordWrapping = resizer.AllowWordWrap.Value;
            }
            else if (!resizer.AllowWordWrap.HasValue
                && textComponent.enableWordWrapping != metadata.OriginalAllowWordWrap)
            {
                textComponent.enableWordWrapping = metadata.OriginalAllowWordWrap;
            }

            if (resizer.AllowAutoSizing.HasValue
                && textComponent.enableAutoSizing != resizer.AllowAutoSizing.Value)
            {
                textComponent.enableAutoSizing = resizer.AllowAutoSizing.Value;
            }
            else if (!resizer.AllowAutoSizing.HasValue
                && textComponent.enableAutoSizing != metadata.OriginalAllowAutoSizing)
            {
                textComponent.enableAutoSizing = metadata.OriginalAllowAutoSizing;
            }

            // Auto Sizing configuration
            if (textComponent.enableAutoSizing)
            {
                if (resizer.MinFontSize.HasValue
                    && resizer.MinFontSize != textComponent.fontSizeMin)
                {
                    textComponent.fontSizeMin = resizer.MinFontSize.Value;
                }

                if (resizer.MaxFontSize.HasValue
                    && resizer.MaxFontSize != textComponent.fontSizeMax)
                {
                    textComponent.fontSizeMax = resizer.MaxFontSize.Value;
                }
            }

            // Spacing
            if (resizer.LineSpacing.HasValue
                && resizer.LineSpacing != textComponent.lineSpacing)
            {
                textComponent.lineSpacing = resizer.LineSpacing.Value;
            }

            if (resizer.WordSpacing.HasValue
                && resizer.WordSpacing != textComponent.wordSpacing)
            {
                textComponent.wordSpacing = resizer.WordSpacing.Value;
            }

            if (resizer.CharacterSpacing.HasValue
                && resizer.CharacterSpacing != textComponent.characterSpacing)
            {
                textComponent.characterSpacing = resizer.CharacterSpacing.Value;
            }

            if (resizer.AllowLeftTrimText)
            {
                //Trim it first so when it initialises it at least trims
                var trimmed = textComponent.text.TrimStart();
                if (textComponent.text != trimmed)
                    textComponent.text = trimmed;
            }

            // Take out the behaviour for now to save perfrormance
            // Only add the behaviour component if it hasn't been added already
            //if (!textComponent.gameObject.TryGetComponent<TextChangedBehaviour>(out var existingBehaviour))
            //{
            //    existingBehaviour = textComponent.gameObject.AddComponent<TextChangedBehaviour>();
            //    // Set the parameter after adding the component
            //    existingBehaviour.SetOptions(resizer);
            //}
            //else if (textComponent.gameObject.TryGetComponent<TextChangedBehaviour>(out var textChangeBehavior))
            //{
            //    Destroy(textChangeBehavior);
            //}
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error applying resizer to {textComponent.name}: {ex}");
        }
    }

    public static TextResizerContract FindAppropriateResizer(string path)
    {
        if (Resizers.TryGetValue(path, out var tryResizer))
            return tryResizer;

        // Check cache first
        if (CachedMatchedResizers.TryGetValue(path, out var cachedResizer))
            return cachedResizer;

        // Try wildcard matching for the remaining resizers
        foreach (var resizerPair in Resizers)
        {
            var resizer = resizerPair.Value;

            if (resizer.Path.Contains("*"))
            {
                //Logger.LogError("Falling to Non compiled!");

                // Convert to Regex
                var pattern = resizer.Path
                    .Replace("/", @"\/")
                    .Replace("(", @"\(")
                    .Replace(")", @"\)")
                    .Replace("*", ".*");

                if (Regex.IsMatch(path, pattern))
                    return resizer;

                // Use our new wildcard matching service
                //var match = _wildcardMatcher.FindMatch(path);
                //if (match != null)
                //{
                //    cache[path] = resizer; // Cache the result
                //    return resizer;
                //}
            }
        }

        return null;
    }

    public static void ApplyAllResizers()
    {
        foreach (var textElement in FindAllTextElements())
            ApplyResizing(textElement);
    }

    [HarmonyPostfix, HarmonyPatch(typeof(GameObject), nameof(GameObject.SetActive), [typeof(bool)])]
    public static void Postfix_GameObject_SetActive(GameObject __instance)
    {
        if (!ResizersLoaded)
            return;

        //TODO: This should be most efficient but we could use Object.Instantiate
        //to get it at as the objects created. But maybe there is post processing occuring after.
        var items = __instance.GetComponentsInChildren<TextMeshProUGUI>();
        foreach (var item in items)
            ApplyResizing(item);
    }
}
