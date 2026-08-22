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
            _logger, _enabled, Paths.BepInExRootPath, new YamlHelper(), new MonoBehaviourAttacher());
        _service.Awake();
    }

    internal void Update()
    {
        if (!_enabled)
            return;

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
}
