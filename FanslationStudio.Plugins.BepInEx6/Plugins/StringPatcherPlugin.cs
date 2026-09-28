using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.Mono;
using FanslationStudio.Plugins.DynamicStrings;
using FanslationStudio.Plugins.SharpYaml;
using FanslationStudio.Plugins.Support;
using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using FanslationStudio.Plugins.UnityShared;
using UnityEngine;

namespace FanslationStudio.Plugins.Plugins;

/// <summary>
/// Used to replace hardcoded strings in IL
/// </summary>
[BepInPlugin($"{MyPluginInfo.PLUGIN_GUID}.DynamicStringPatcher", "DynamicStringPatcher", MyPluginInfo.PLUGIN_VERSION)]
public class StringPatcherPlugin : BaseUnityPlugin
{
    public static bool _enabled = false;
    public StringPatcherService StringPatcherService;

    private void Awake()
    {
        _enabled = PluginConfig.File.Bind("DynamicStringPatcher", "Enabled", false,
            "Replace dynamic strings that are hardcoded in code, using the translated dynamic string files").Value;

        var resourcePath = PluginConfig.File.Bind("DynamicStringPatcher", "ResourcePath", "./english",
            "Folder (relative to BepInEx/) containing the translated dynamic string files").Value;
        var filePattern = PluginConfig.File.Bind("DynamicStringPatcher", "FilePattern", DynamicStringContract.DefaultFilePattern,
            "Translated dynamic string files to load from ResourcePath (* matches anything, case is ignored). Every match is loaded, alphabetically").Value;

        StringPatcherService = new StringPatcherService(new BepInEx6Logger(base.Logger), 
            _enabled,
            new Harmony($"{MyPluginInfo.PLUGIN_GUID}.DynamicStringPatcher"),
            resourcePath,
            filePattern,
            Paths.BepInExRootPath,
            new YamlHelper());

        StringPatcherService.Awake();

        if (!_enabled)
            return;

        // Static engine event rather than Update(): see TextResizerPlugin for why.
        Canvas.willRenderCanvases += Tick;
    }

    private void Tick()
    {
        // Only needed once: EnsurePatched applies the patches on the first frame.
        Canvas.willRenderCanvases -= Tick;

        try
        {
            StringPatcherService.EnsurePatched();
        }
        catch (Exception ex)
        {
            Logger.LogError($"[DynamicStringPatcher] Tick threw: {ex}");
        }
    }
}