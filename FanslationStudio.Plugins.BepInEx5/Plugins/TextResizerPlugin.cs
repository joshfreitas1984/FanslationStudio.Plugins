using System;
using BepInEx;
using FanslationStudio.Plugins.Shared;
using FanslationStudio.Plugins.Support;
using FanslationStudio.Plugins.TextResizer;
using FanslationStudio.Plugins.YamlDotNet;
using UnityEngine;
using FanslationStudio.Plugins.UnityShared;

namespace FanslationStudio.Plugins.Plugins;

// We need to wrap any services we want to use in the plugin itself,
// because BepInEx doesn't allow us to use typed references to the service in the plugin,
// and we don't want to use reflection everywhere.
public class TextResizerServiceWrapper : TextResizerService
{
    public TextResizerServiceWrapper(IPluginLogger logger, bool enabled, string bepinexRootPath, IYamlHelper yamlHelper)
        : base(logger, enabled, bepinexRootPath, yamlHelper, new MonoElementFinder())
    {
    }
}

[BepInPlugin($"{MyPluginInfo.PLUGIN_GUID}.TextResizer", "TextResizer", MyPluginInfo.PLUGIN_VERSION)]
internal class TextResizerPlugin : BaseUnityPlugin
{
    // Cannot use typed references b
    private static TextResizerServiceWrapper _service;
    private static bool _enabled = true;

    private void Awake()
    {
        _enabled = PluginConfig.File.Bind("TextResizer", "Enabled", true,
            "Apply text resizers from BepInEx/resizers/*.yaml").Value;

        if (!_enabled)
            return;

        _service = new TextResizerServiceWrapper(
             new BepInEx5Logger(Logger),
             _enabled, Paths.BepInExRootPath, new YamlHelper());
        _service.Awake();

        // Some hosts disable/deactivate freshly-injected plugin GameObjects/components shortly
        // after chainloader startup (observed on at least one BepInEx5 title), which silently
        // stops MonoBehaviour.Update from ever firing again - with no exception and no managed
        // stack trace at the point of disable, so it can't be caught or worked around from here.
        // Canvas.willRenderCanvases is a static engine event: subscribing to it doesn't add
        // anything to the scene graph, so it isn't affected by that behaviour, and it fires every
        // frame the game is about to render any canvas (which every TMPro/UGUI game does).
        Canvas.willRenderCanvases += Tick;
    }

    private void Tick()
    {
        if (!_enabled)
            return;

        // Canvas.willRenderCanvases is invoked directly by the engine for every subscriber;
        // an uncaught exception here could take down other listeners (including Unity's own
        // UI redraw for this frame), so keep this method from ever throwing out to the event.
        try
        {
            _service.EnsurePatched();
            _service.CheckForSceneChange();
        }
        catch (Exception ex)
        {
            Logger.LogError($"[TextResizer] Tick threw: {ex}");
        }
    }
}
