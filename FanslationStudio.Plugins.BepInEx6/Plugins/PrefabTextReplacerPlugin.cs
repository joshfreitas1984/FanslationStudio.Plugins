using System;
using BepInEx;
using BepInEx.Unity.Mono;
using FanslationStudio.Plugins.PrefabText;
using FanslationStudio.Plugins.Shared;
using FanslationStudio.Plugins.SharpYaml;
using HarmonyLib;
using UnityEngine;
using FanslationStudio.Plugins.UnityShared;

namespace FanslationStudio.Plugins.Plugins;

/// <summary>
/// Used to replace hardcoded strings in prefabs
/// </summary>
[BepInPlugin($"{MyPluginInfo.PLUGIN_GUID}.PrefabTextReplacer", "PrefabTextReplacer", MyPluginInfo.PLUGIN_VERSION)]
public class PrefabTextReplacerPlugin : BaseUnityPlugin
{
    private static IPluginLogger _logger;
    private static PrefabTextReplacerService _service;

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

        _logger = new BepInEx6Logger(base.Logger);
        _service = new PrefabTextReplacerService(_logger,
            enabled,
            new Harmony($"{MyPluginInfo.PLUGIN_GUID}.PrefabTextReplacer"),
            resourcePath,
            filePattern,
            Paths.BepInExRootPath,
            new YamlHelper(),
            new MonoElementFinder());
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
            _logger.LogError($"[PrefabTextReplacer] Tick threw: {ex}");
        }
    }
}
