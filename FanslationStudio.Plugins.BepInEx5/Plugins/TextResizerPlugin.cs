using BepInEx;
using FanslationStudio.Plugins.Shared;
using FanslationStudio.Plugins.Support;
using FanslationStudio.Plugins.TextResizer;
using FanslationStudio.Plugins.YamlDotNet;
using UnityEngine;

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

        _service = new TextResizerServiceWrapper(
             new BepInEx5Logger(Logger),
             _enabled, Paths.BepInExRootPath, new YamlHelper());
        _service.Awake();

        TextResizerEditorUi.Configure(_service);
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
        {
            _service.AddResizersForScene();
            TextResizerEditorUi.Open();
        }

        var x = UnityInput.Current.mousePosition.x;
        var y = UnityInput.Current.mousePosition.y;
        var z = UnityInput.Current.mousePosition.z;

        if (UnityInput.Current.GetKeyDown(_addResizerAtCursorHotKey))
        {
            _service.AddResizersAtCursor(x, y, z);
            TextResizerEditorUi.Open();
        }

        TextResizerEditorUi.Tick();
    }
}
