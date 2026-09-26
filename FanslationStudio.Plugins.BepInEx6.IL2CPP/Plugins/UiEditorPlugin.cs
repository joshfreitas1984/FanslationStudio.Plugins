using BepInEx;
using BepInEx.Unity.IL2CPP;
using FanslationStudio.Plugins.SharpYaml;
using FanslationStudio.Plugins.UnityShared.Editor;
using HarmonyLib;
using UnityEngine;

namespace FanslationStudio.Plugins.Plugins;

[BepInPlugin($"{MyPluginInfo.PLUGIN_GUID}.UIEditor", "UIEditor", MyPluginInfo.PLUGIN_VERSION)]
public class UiEditorPlugin : BasePlugin
{
    private static int _lastTickedFrame = -1;

    public override void Load()
    {
        UiEditorHost.Initialize(new BepInEx6Logger(base.Log), Config, new YamlHelper(), Paths.BepInExRootPath,
            $"{MyPluginInfo.PLUGIN_GUID}.UIEditor");
        if (!UiEditorHost.Enabled)
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
    }
}
