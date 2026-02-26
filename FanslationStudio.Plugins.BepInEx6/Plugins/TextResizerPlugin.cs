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

    private KeyCode _addResizerAtCursorHotKey = KeyCode.KeypadMinus;
    private KeyCode _reloadHotkey = KeyCode.KeypadPlus;
    private KeyCode _addResizerHotKey = KeyCode.KeypadMultiply;

    private void Awake()
    {
        _enabled = Config.Bind("General",
            "TextResizerEnabled",
            true,
            "Enable Text Resizer plugin").Value;

        if (!_enabled)
            return;

        _logger = new BepInEx6Logger(base.Logger);
        _service = new TextResizerService(
            _logger, _enabled, Paths.BepInExRootPath, new YamlHelper());
        _service.Awake();
    }

    internal void Update()
    {
        if (!_enabled)
            return;

        if (Input.GetKeyDown(_reloadHotkey))
            _service.Reload();

        if (Input.GetKeyDown(_addResizerHotKey))
            _service.AddResizersForScene();

        var x = Input.mousePosition.x;
        var y = Input.mousePosition.y;
        var z = Input.mousePosition.z;

        if (Input.GetKeyDown(_addResizerAtCursorHotKey))
            _service.AddResizersAtCursor(x, y, z);
    }
}
