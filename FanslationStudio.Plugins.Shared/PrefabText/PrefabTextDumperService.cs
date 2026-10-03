using FanslationStudio.Plugins.DynamicStrings;
using FanslationStudio.Plugins.Shared;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using UnityEngine;

namespace FanslationStudio.Plugins.PrefabText;

/// <summary>
/// Used to get hardcoded strings out of prefabs so we can translate them
/// </summary>
public class PrefabTextDumperService
{
    public static IPluginLogger Logger;
    public static string RegexPattern;
    public static string GameDataPath;

    // Host-runtime-specific finder for GameObjects/Components. See IPrefabTextFinder for why this
    // can't be a direct Resources.FindObjectsOfTypeAll/GetComponentsInChildren call from Shared.
    private readonly IPrefabTextFinder _elementFinder;
    private readonly Regex _matchRegex;

    // Per component type: the serialized text field, or null when the type has none (always the
    // case under IL2CPP, where the wrappers expose it as a property) and the finder reads it.
    private readonly Dictionary<Type, FieldInfo> _textFields = [];

    public PrefabTextDumperService(IPluginLogger logger, string regexPattern, string gameDataPath,
        IPrefabTextFinder elementFinder)
    {
        Logger = logger;
        RegexPattern = regexPattern;
        GameDataPath = gameDataPath;
        _elementFinder = elementFinder;
        _matchRegex = DynamicStringSupport.CreateMatchRegex(regexPattern);
    }

    /// <summary>
    /// Writes prefabText.txt into <paramref name="outputPath"/>. Returns the number of strings dumped.
    /// Only finds objects Unity has already loaded, so call this once the game is past its menus/scenes.
    /// </summary>
    public int DumpAllPrefabTexts(string outputPath)
    {
        try
        {
            var exportedStrings = new HashSet<string>();

            Logger.LogWarning($"Scanning for prefabs in: {GameDataPath}");

            LoadPrefabsFromResources(exportedStrings);
            LoadPrefabsFromAssetBundles(GameDataPath, exportedStrings);

            if (exportedStrings.Count > 0)
            {
                File.WriteAllLines(Path.Combine(outputPath, PrefabTextContract.FileName), exportedStrings.OrderBy(s => s));
                Logger.LogWarning($"Exported {exportedStrings.Count} strings from prefabs to {outputPath}");
            }
            else
            {
                Logger.LogWarning("No prefab strings found matching the pattern");
            }

            return exportedStrings.Count;
        }
        catch (Exception ex)
        {
            Logger.LogError($"Error dumping prefab texts: {ex}");
            return 0;
        }
    }

    private void LoadPrefabsFromResources(HashSet<string> exportedStrings)
    {
        try
        {
            // Every loaded text component (scene objects, inactive objects and prefab assets),
            // filtered by type in the engine rather than walking every GameObject's components.
            var components = _elementFinder.FindAllTextComponentsInResources();
            Logger.LogWarning($"Found {components.Length} text components in Resources");

            ExtractText(components, exportedStrings);
        }
        catch (Exception ex)
        {
            Logger.LogError($"Error loading prefabs from Resources: {ex.Message}");
        }
    }

    private void LoadPrefabsFromAssetBundles(string gameDataPath, HashSet<string> exportedStrings)
    {
        try
        {
            var assetBundleFiles = Directory.GetFiles(gameDataPath, "*", SearchOption.AllDirectories)
                .Where(f =>
                {
                    var fileName = Path.GetFileName(f).ToLowerInvariant();
                    var ext = Path.GetExtension(f).ToLowerInvariant();

                    if (fileName == "globalgamemanagers" || fileName == "globalgamemanagers.assets" ||
                        fileName == "resources.assets" || fileName.StartsWith("sharedassets") ||
                        fileName.StartsWith("level"))
                        return false;

                    // .bundle is the extension used by Unity's Addressables/AssetBundle Browser
                    // output and is very common for shipped external bundles - the original
                    // filter missed it entirely.
                    return ext == ".assets" || ext == ".unity3d" || ext == ".assetbundle" || ext == ".bundle";
                })
                .ToList();

            Logger.LogWarning($"Found {assetBundleFiles.Count} potential asset bundle files");

            if (assetBundleFiles.Count == 0)
            {
                // sharedassets*/level*/resources.assets/globalgamemanagers are intentionally
                // excluded above - they're Unity's internal monolithic data files, not standalone
                // asset bundles, and can't be opened via AssetBundle.LoadFromFile. A game that
                // packs everything into those files (no separate .assets/.unity3d/.bundle files)
                // simply has no external bundles for this scan to find - that's expected, not a
                // bug. Prefab text baked into those files can only be reached via objects Unity
                // has actually loaded into memory (see LoadPrefabsFromResources), which requires
                // triggering the dump once relevant scenes/UI have loaded rather than at startup.
                Logger.LogWarning("No external asset bundle files found - this game likely packs all assets into sharedassets*/level*/resources.assets files instead, which aren't scannable this way. Try triggering the dump again once you're further into the game (menus/scenes loaded), since LoadPrefabsFromResources only sees objects Unity has already loaded into memory.");
            }

            foreach (var bundleFile in assetBundleFiles)
            {
                try
                {
                    // Delegated to the host-specific finder (see IPrefabTextFinder) - the whole
                    // AssetBundle.LoadFromFile/GetAllAssetNames/LoadAsset/Unload sequence throws
                    // MissingMethodException at runtime under IL2CPP when called from Shared.
                    var gameObjects = _elementFinder.LoadGameObjectsFromAssetBundle(bundleFile);
                    Logger.LogWarning($"Loaded bundle: {Path.GetFileName(bundleFile)} with {gameObjects.Length} GameObject assets");

                    foreach (var gameObject in gameObjects)
                    {
                        if (gameObject != null)
                        {
                            ExtractTextFromGameObject(gameObject, exportedStrings);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogDebug($"Skipping file {Path.GetFileName(bundleFile)}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogError($"Error loading prefabs from asset bundles: {ex.Message}");
        }
    }

    private void ExtractTextFromGameObject(GameObject gameObject, HashSet<string> exportedStrings)
    {
        try
        {
            // Delegated to the host-specific finder (see IPrefabTextFinder) - calling
            // GetComponentsInChildren(Type, bool) directly from Shared throws MissingMethodException
            // at runtime under IL2CPP.
            ExtractText(_elementFinder.GetTextComponentsInChildren(gameObject, true), exportedStrings);
        }
        catch (Exception ex)
        {
            Logger.LogError($"Error extracting text from {gameObject.name}: {ex.Message}");
        }
    }

    private void ExtractText(Component[] components, HashSet<string> exportedStrings)
    {
        foreach (var component in components)
        {
            if (component == null)
                continue;

            var text = GetValidTextProperty(component);
            if (!string.IsNullOrEmpty(text))
            {
                var cleanedText = text.Replace("\n", "\\n").Replace("\r", "");
                exportedStrings.Add(cleanedText);
            }
        }
    }

    private string GetValidTextProperty(Component component)
    {
        // Mono reads the serialized field (as the replacer does). Under IL2CPP the wrappers have
        // no such field, so the host finder reads the text instead.
        var textField = GetTextField(component.GetType());
        var textValue = textField != null
            ? textField.GetValue(component) as string
            : _elementFinder.GetText(component);

        return !string.IsNullOrEmpty(textValue) && _matchRegex.IsMatch(textValue) ? textValue : string.Empty;
    }

    private FieldInfo GetTextField(Type type)
    {
        if (_textFields.TryGetValue(type, out var textField))
            return textField;

        // Type.GetField with BindingFlags.NonPublic does NOT search inherited members unless
        // BindingFlags.FlattenHierarchy is also specified. TextMeshProUGUI's "m_text" field is
        // declared on its base class TMP_Text, not on TextMeshProUGUI itself, so without
        // FlattenHierarchy this always returned null for every TMP component - silently skipping
        // the vast majority of in-game text. UnityEngine.UI.Text's "m_Text" happened to be
        // declared directly on Text, so that path worked by coincidence.
        textField =
            type.GetField("m_text", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy)
            ?? type.GetField("m_Text", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy);

        if (textField != null && textField.FieldType != typeof(string))
            textField = null;

        _textFields[type] = textField;
        return textField;
    }
}
