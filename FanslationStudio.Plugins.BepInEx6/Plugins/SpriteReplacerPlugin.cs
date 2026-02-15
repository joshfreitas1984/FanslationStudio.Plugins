using BepInEx;
using BepInEx.Unity.Mono;
using FanslationStudio.Plugins.Shared;
using FanslationStudio.Plugins.SharpYaml;
using FanslationStudio.Plugins.Sprites;
using UnityEngine;

namespace FanslationStudio.Plugins.Plugins;

[BepInPlugin($"{MyPluginInfo.PLUGIN_GUID}.SpriteReplacer", "SpriteReplacer", MyPluginInfo.PLUGIN_VERSION)]
public class SpriteReplacerPlugin : BaseUnityPlugin
{
    private static IPluginLogger _logger;    
    private static SpriteReplacerService _service;
    private KeyCode _addAtCursorHotKey = KeyCode.F1;
    private KeyCode _addAllHotKey = KeyCode.F2;
    private KeyCode _reloadHotkey = KeyCode.F3;
    private static bool _enabled;

    private void Awake()
    {
        _enabled = Config.Bind("General",
            "Enabled",
            false,
            "Turn on sprite replacer plugin").Value;

        if (!_enabled)
            return;

        _logger = new BepInEx6Logger(base.Logger);
        _service = new SpriteReplacerService(
            _logger, _enabled, Paths.BepInExRootPath, new YamlHelper());

        _service.Awake();
    }

    internal void Update()
    {
        if (!_enabled)
            return;

        if (Input.GetKeyDown(_reloadHotkey))
            _service.Reload();

        if (Input.GetKeyDown(_addAllHotKey))
            _service.AddAll();

        var x = Input.mousePosition.x;
        var y = Input.mousePosition.y;
        var z = Input.mousePosition.z;

        if (Input.GetKeyDown(_addAtCursorHotKey))
            _service.AddAtCursor(x, y, z);
    }
}
