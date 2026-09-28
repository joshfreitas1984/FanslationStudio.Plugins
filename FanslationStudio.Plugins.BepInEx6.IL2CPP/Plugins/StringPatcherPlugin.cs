using BepInEx;
using BepInEx.Unity.IL2CPP;
using FanslationStudio.Plugins.DynamicStrings;
using FanslationStudio.Plugins.SharpYaml;
using HarmonyLib;
using System;
using UnityEngine;
using FanslationStudio.Plugins.UnityShared;

namespace FanslationStudio.Plugins.Plugins;

/// <summary>
/// Used to replace hardcoded strings in IL
/// </summary>
[BepInPlugin($"{MyPluginInfo.PLUGIN_GUID}.DynamicStringPatcher", "DynamicStringPatcher", MyPluginInfo.PLUGIN_VERSION)]
public class StringPatcherPlugin : BasePlugin
{
    public static bool _enabled = false;
    public static StringPatcherService StringPatcherService;

    public override void Load()
    {
        _enabled = PluginConfig.File.Bind("DynamicStringPatcher", "Enabled", false,
            "Replace dynamic strings that are hardcoded in code, using the translated dynamic string files").Value;

        if (!_enabled)
            return;

        var resourcePath = PluginConfig.File.Bind("DynamicStringPatcher", "ResourcePath", "./english",
            "Folder (relative to BepInEx/) containing the translated dynamic string files").Value;
        var filePattern = PluginConfig.File.Bind("DynamicStringPatcher", "FilePattern", DynamicStringContract.DefaultFilePattern,
            "Translated dynamic string files to load from ResourcePath (* matches anything, case is ignored). Every match is loaded, alphabetically").Value;

        StringPatcherService = new StringPatcherService(new BepInEx6Logger(base.Log),
            _enabled,
            new Harmony($"{MyPluginInfo.PLUGIN_GUID}.DynamicStringPatcher"),
            resourcePath,
            filePattern,
            Paths.BepInExRootPath,
            new YamlHelper());

        StringPatcherService.Awake();

        // BasePlugin is not ticked by Unity, and attaching a custom MonoBehaviour needs
        // ClassInjector registration, which crashes under IL2CPP (see copilot-instructions).
        // Patching is deferred to the first frame instead, via a Time.deltaTime getter postfix -
        // the same tick TextResizerPlugin and UiEditorPlugin use.
        var harmony = new Harmony($"{MyPluginInfo.PLUGIN_GUID}.DynamicStringPatcher.Tick");
        var deltaTimeGetter = AccessTools.PropertyGetter(typeof(Time), nameof(Time.deltaTime));
        harmony.Patch(deltaTimeGetter, postfix: new HarmonyMethod(typeof(StringPatcherPlugin), nameof(OnDeltaTimeRead)));
    }

    private static bool _ticked;

    private static void OnDeltaTimeRead()
    {
        // Only one tick is needed: EnsurePatched applies the patches once, on the first frame.
        if (_ticked)
            return;
        _ticked = true;

        try
        {
            if (_enabled)
                StringPatcherService.EnsurePatched();
        }
        catch (Exception ex)
        {
            FanslationStudio.Plugins.DynamicStrings.StringPatcherService.Logger?.LogError($"[DynamicStringPatcher] Patching threw: {ex}");
        }
    }
}
