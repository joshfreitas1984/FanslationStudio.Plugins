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

    public void Awake()
    {        
        if (!Enabled)
            return;

        Logger.LogWarning("Prefab Text Dumper plugin is starting...");
        Harmony.CreateAndPatchAll(typeof(PrefabTextDumperService));
        Logger.LogWarning("Prefab Text Dumper plugin patching complete!");

        DumpAllPrefabTexts();
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
            var components = gameObject.GetComponentsInChildren<Component>(true);
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
