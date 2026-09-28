using System;
using BepInEx;
using FanslationStudio.Plugins.PrefabText;
using FanslationStudio.Plugins.Shared;
using FanslationStudio.Plugins.Support;
using FanslationStudio.Plugins.YamlDotNet;
using HarmonyLib;
using UnityEngine;
using FanslationStudio.Plugins.UnityShared;

namespace FanslationStudio.Plugins.Plugins;

// We need to wrap any services we want to use in the plugin itself,
// because BepInEx doesn't allow us to use typed references to the service in the plugin,
// and we don't want to use reflection everywhere.
public class PrefabTextReplacerServiceWrapper : PrefabTextReplacerService
{
    public PrefabTextReplacerServiceWrapper(IPluginLogger logger, bool enabled, Harmony harmony, string resourcePath, string filePattern, string bepinExRootPath, IYamlHelper yamlHelper)
        : base(logger, enabled, harmony, resourcePath, filePattern, bepinExRootPath, yamlHelper, new MonoElementFinder())
    {
    }
}

/// <summary>
/// Used to replace hardcoded strings in prefabs
/// </summary>
[BepInPlugin($"{MyPluginInfo.PLUGIN_GUID}.PrefabTextReplacer", "PrefabTextReplacer", MyPluginInfo.PLUGIN_VERSION)]
internal class PrefabTextReplacerPlugin : BaseUnityPlugin
{
    private static PrefabTextReplacerServiceWrapper _service;

    private void Awake()
    {
        var enabled = PluginConfig.File.Bind("PrefabTextReplacer", "Enabled", false,
            "Replace text that is hardcoded in prefabs, using the translated prefab text files").Value;
        var resourcePath = PluginConfig.File.Bind("PrefabTextReplacer", "ResourcePath", "./english",
            "Folder (relative to BepInEx/) containing the translated prefab text files").Value;
        var filePattern = PluginConfig.File.Bind("PrefabTextReplacer", "FilePattern", PrefabTextContract.DefaultFilePattern,
            "Translated prefab text files to load from ResourcePath (* matches anything, case is ignored). Every match is loaded, alphabetically; later files win").Value;

        if (!enabled)
            return;

        _service = new PrefabTextReplacerServiceWrapper(new BepInEx5Logger(Logger),
            enabled,
            new Harmony($"{MyPluginInfo.PLUGIN_GUID}.PrefabTextReplacer"),
            resourcePath,
            filePattern,
            Paths.BepInExRootPath,
            new YamlHelper());
        _service.Awake();

        // Static engine event rather than Update(): see TextResizerPlugin for why.
        Canvas.willRenderCanvases += Tick;
    }

    private void Tick()
    {
        // Only needed once - after that the Harmony/sceneLoaded hooks take over.
        Canvas.willRenderCanvases -= Tick;

        try
        {
            _service.EnsurePatched();
        }
        catch (Exception ex)
        {
            Logger.LogError($"[PrefabTextReplacer] Tick threw: {ex}");
        }
    }
}
