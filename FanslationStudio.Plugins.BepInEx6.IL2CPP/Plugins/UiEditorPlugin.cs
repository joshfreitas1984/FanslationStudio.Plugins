using BepInEx;
using BepInEx.Unity.IL2CPP;
using FanslationStudio.Plugins.SharpYaml;
using FanslationStudio.Plugins.UnityShared.Editor;
using FanslationStudio.Plugins.UnityShared.Update;
using HarmonyLib;
using UnityEngine;

namespace FanslationStudio.Plugins.Plugins;

[BepInPlugin($"{MyPluginInfo.PLUGIN_GUID}.UIEditor", "UIEditor", MyPluginInfo.PLUGIN_VERSION)]
public class UiEditorPlugin : BasePlugin
{
    private static int _lastTickedFrame = -1;

    public override void Load()
    {
        UiEditorHost.Initialize(new BepInEx6Logger(base.Log), new YamlHelper(), Paths.BepInExRootPath,
            $"{MyPluginInfo.PLUGIN_GUID}.UIEditor", Paths.ManagedPath, Paths.GameDataPath, new Il2CppElementFinder());

        // Runs even when the UI Editor is disabled. Only pure .NET happens here (config, a file read and a
        // background HTTP task); the prompt UI is built later, from the tick.
        UpdateHost.Initialize(new BepInEx6Logger(base.Log), Paths.BepInExRootPath, Paths.GameRootPath);

        if (!UiEditorHost.Enabled && !UpdateHost.Enabled)
            return;

        // Per-frame tick via a Time.deltaTime getter postfix - the only tick mechanism that is
        // safe to set up from Load() under IL2CPP. See TextResizerPlugin.Load for the details.
        var harmony = new Harmony($"{MyPluginInfo.PLUGIN_GUID}.UIEditor");
        var deltaTimeGetter = AccessTools.PropertyGetter(typeof(Time), nameof(Time.deltaTime));
        harmony.Patch(deltaTimeGetter, postfix: new HarmonyMethod(typeof(UiEditorPlugin), nameof(OnDeltaTimeRead)));
    }

    private static void OnDeltaTimeRead()
    {
        // Time.deltaTime can be read many times per frame - only tick once per frame.
        var frame = Time.frameCount;
        if (frame == _lastTickedFrame)
            return;

        _lastTickedFrame = frame;
        UiEditorHost.Tick();
        UpdateHost.Tick();
    }
}
