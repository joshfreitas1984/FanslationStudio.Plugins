using FanslationStudio.Plugins.Shared;
using HarmonyLib;
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
    public static bool Enabled = false;
    public static string RegexPattern;
    public static string DumpFilePath;
    public static string ManagedPath;
    private readonly string BepinExPath;

    public PrefabTextDumperService(IPluginLogger logger,
        string dumpFilePath, string regexPattern, bool enabled, string managedPath, string bepinExPath)
    {
        Logger = logger;
        DumpFilePath = dumpFilePath;
        RegexPattern = regexPattern;
        Enabled = enabled;
        ManagedPath = managedPath;
        BepinExPath = bepinExPath;
    }

    // Tracks whether Harmony patching has been applied yet. Patching is deferred (see
    // EnsurePatched) rather than done immediately in Awake/Load, because under IL2CPP,
    // Harmony resolves Il2CppType tokens for patch parameter types (e.g. GameObject) via
    // Il2CppType.From. If this runs before Unity has naturally initialized the relevant
    // modules, it can force their static cctor to run reentrantly inside Il2CppInterop's
    // generic-method hook, corrupting memory (AccessViolationException) and crashing the game.
    private static bool _patched = false;

    public void Awake()
    {        
        if (!Enabled)
            return;

        Logger.LogWarning("Prefab Text Dumper plugin is starting...");

        DumpAllPrefabTexts();
    }

    /// <summary>
    /// Applies the Harmony patches. Must be called after at least one frame/scene has run
    /// (e.g. from the plugin's Update, not from Awake/Load) so Unity has had a chance to
    /// naturally initialize the relevant modules before Harmony/Il2CppInterop tries to
    /// resolve their type tokens - doing this too early can crash the game under IL2CPP.
    /// </summary>
    public void EnsurePatched()
    {
        if (_patched || !Enabled)
            return;

        Harmony.CreateAndPatchAll(typeof(PrefabTextDumperService));
        _patched = true;
        Logger.LogWarning("Prefab Text Dumper plugin patching complete!");
    }

    public void DumpAllPrefabTexts()
    {
        try
        {
            var exportedStrings = new HashSet<string>();
            var gameDataPath = Path.GetFullPath(Path.Combine(ManagedPath, ".."));

            Logger.LogWarning($"Scanning for prefabs in: {gameDataPath}");

            LoadPrefabsFromResources(exportedStrings);
            LoadPrefabsFromAssetBundles(gameDataPath, exportedStrings);

            if (exportedStrings.Count > 0)
            {
                var outputPath = Path.Combine(BepinExPath, DumpFilePath);
                File.WriteAllLines($"{outputPath}/dumpedPrefabText.txt", exportedStrings.OrderBy(s => s));
                Logger.LogWarning($"Exported {exportedStrings.Count} strings from prefabs to {DumpFilePath}");
            }
            else
            {
                Logger.LogWarning("No prefab strings found matching the pattern");
            }
        }
        catch (Exception ex)
        {
            Logger.LogError($"Error dumping prefab texts: {ex}");
        }
    }

    private void LoadPrefabsFromResources(HashSet<string> exportedStrings)
    {
        try
        {
            var allGameObjects = Resources.FindObjectsOfTypeAll(typeof(GameObject));
            Logger.LogWarning($"Found {allGameObjects.Length} GameObjects in Resources");

            foreach (var obj in allGameObjects)
            {
                if (obj is GameObject gameObject)
                {
                    ExtractTextFromGameObject(gameObject, exportedStrings);
                }
            }
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
                .Where(f => {
                    var fileName = Path.GetFileName(f).ToLowerInvariant();
                    var ext = Path.GetExtension(f).ToLowerInvariant();

                    if (fileName == "globalgamemanagers" || fileName == "globalgamemanagers.assets" ||
                        fileName == "resources.assets" || fileName.StartsWith("sharedassets") ||
                        fileName.StartsWith("level"))
                        return false;

                    return (ext == ".assets" || ext == ".unity3d" || ext == ".assetbundle");                       
                })
                .ToList();

            Logger.LogWarning($"Found {assetBundleFiles.Count} potential asset bundle files");

            foreach (var bundleFile in assetBundleFiles)
            {
                try
                {
                    var bundle = AssetBundle.LoadFromFile(bundleFile);
                    if (bundle != null)
                    {
                        var allAssetNames = bundle.GetAllAssetNames();
                        Logger.LogWarning($"Loaded bundle: {Path.GetFileName(bundleFile)} with {allAssetNames.Length} assets");

                        foreach (var assetName in allAssetNames)
                        {
                            var asset = bundle.LoadAsset(assetName);
                            if (asset is GameObject gameObject)
                            {
                                ExtractTextFromGameObject(gameObject, exportedStrings);
                            }
                        }

                        bundle.Unload(false);
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
            // Note: using the non-generic Type overload here instead of GetComponentsInChildren<Component>(true)
            // because that generic instantiation isn't guaranteed to exist under IL2CPP interop and can
            // throw a MissingMethodException at runtime.
            var components = gameObject.GetComponentsInChildren(typeof(Component), true);
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
        catch (Exception ex)
        {
            Logger.LogError($"Error extracting text from {gameObject.name}: {ex.Message}");
        }
    }

    private static string GetValidTextProperty(object component)
    {
        var response = string.Empty;

        if (component is null)
            return response;

        var type = component.GetType();

        if (type is null)
            return response;

        var textField =
            type.GetField("m_text", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            ?? type.GetField("m_Text", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

        if (textField != null && textField.FieldType == typeof(string))
        {
            var textValue = textField.GetValue(component) as string;
            if (!string.IsNullOrEmpty(textValue) && Regex.IsMatch(textValue, RegexPattern))
                response = textValue;
        }

        return response;
    }
}
