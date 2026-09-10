using System;
using BepInEx;
using BepInEx.Unity.Mono;
using FanslationStudio.Plugins.Shared;
using FanslationStudio.Plugins.SharpYaml;
using FanslationStudio.Plugins.TextResizer;
using UnityEngine;

namespace FanslationStudio.Plugins.Plugins;

[BepInPlugin($"{MyPluginInfo.PLUGIN_GUID}.TextResizer", "TextResizer", MyPluginInfo.PLUGIN_VERSION)]
public class TextResizerPlugin : BaseUnityPlugin
{
    private static IPluginLogger _logger;
    private static TextResizerService _service;
    private static bool _enabled = true;

    private KeyCode _addResizerAtCursorHotKey;
    private KeyCode _reloadHotkey;
    private KeyCode _addResizerHotKey;


    private void Awake()
    {
        _enabled = Config.Bind("General",
            "TextResizerEnabled",
            true,
            "Enable Text Resizer plugin").Value;

        _addResizerAtCursorHotKey = Config.Bind("Hotkeys",
            "AddResizerAtCursorHotKey",
            KeyCode.KeypadMinus,
            "Adds a text resizer at the cursor position").Value;
        _reloadHotkey = Config.Bind("Hotkeys",
            "ReloadHotkey",
            KeyCode.KeypadPlus,
            "Reloads the text resizer configuration").Value;
        _addResizerHotKey = Config.Bind("Hotkeys",
            "AddResizerHotKey",
            KeyCode.KeypadMultiply,
            "Adds text resizers for every element in the current scene").Value;

        if (!_enabled)
            return;

        _logger = new BepInEx6Logger(base.Logger);
        _service = new TextResizerService(
            _logger, _enabled, Paths.BepInExRootPath, new YamlHelper(), new MonoElementFinder());
        _service.Awake();

        // Some hosts disable/deactivate freshly-injected plugin GameObjects/components shortly
        // after chainloader startup, which silently stops MonoBehaviour.Update from ever firing
        // again. Canvas.willRenderCanvases is a static engine event: subscribing to it doesn't
        // add anything to the scene graph, so it isn't affected by that behaviour, and it fires
        // every frame the game is about to render any canvas (which every TMPro/UGUI game does).
        Canvas.willRenderCanvases += Tick;
    }

    private void Tick()
    {
        if (!_enabled)
            return;

        try
        {
            _service.EnsurePatched();
            _service.CheckForSceneChange();

            if (UnityInput.Current.GetKeyDown(_reloadHotkey))
                _service.Reload();

            if (UnityInput.Current.GetKeyDown(_addResizerHotKey))
                _service.AddResizersForScene();

            var x = UnityInput.Current.mousePosition.x;
            var y = UnityInput.Current.mousePosition.y;
            var z = UnityInput.Current.mousePosition.z;

            if (UnityInput.Current.GetKeyDown(_addResizerAtCursorHotKey))
                _service.AddResizersAtCursor(x, y, z);
        }
        catch (Exception ex)
        {
            _logger.LogError($"[TextResizer] Tick threw: {ex}");
        }
    }
}
