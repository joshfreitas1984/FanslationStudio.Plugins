using BepInEx;
using FanslationStudio.Plugins.Shared;
using FanslationStudio.Plugins.Sprites;
using FanslationStudio.Plugins.Support;
using FanslationStudio.Plugins.YamlDotNet;
using UnityEngine;

namespace FanslationStudio.Plugins.Plugins;

// We need to wrap any services we want to use in the plugin itself,
// because BepInEx doesn't allow us to use typed references to the service in the plugin,
// and we don't want to use reflection everywhere.
public class SpriteReplacerServiceWrapper : SpriteReplacerService
{
    public SpriteReplacerServiceWrapper(IPluginLogger logger, bool enabled, string bepinexRootPath, IYamlHelper yamlHelper, ISpriteElementFinder elementFinder)
        : base(logger, enabled, bepinexRootPath, yamlHelper, elementFinder)
    {
    }
}

[BepInPlugin($"{MyPluginInfo.PLUGIN_GUID}.SpriteReplacer", "SpriteReplacer", MyPluginInfo.PLUGIN_VERSION)]
public class SpriteReplacerPlugin : BaseUnityPlugin
{
    private static SpriteReplacerServiceWrapper _service;
    private KeyCode _addAtCursorHotKey;
    private KeyCode _addAllHotKey;
    private KeyCode _reloadHotkey;
    private static bool _enabled;

    private void Awake()
    {
        _enabled = Config.Bind("General",
            "Enabled",
            false,
            "Turn on sprite replacer plugin").Value;

        _addAtCursorHotKey = Config.Bind("Hotkeys",
            "AddAtCursorHotKey",
            KeyCode.F1,
            "Adds a sprite replacer at the cursor position").Value;
        _addAllHotKey = Config.Bind("Hotkeys",
            "AddAllHotKey",
            KeyCode.F2,
            "Adds sprite replacers for every sprite in the current scene").Value;
        _reloadHotkey = Config.Bind("Hotkeys",
            "ReloadHotkey",
            KeyCode.F3,
            "Reloads the sprite replacer configuration").Value;

        if (!_enabled)
            return;

        _service = new SpriteReplacerServiceWrapper(
            new BepInEx5Logger(Logger), _enabled, Paths.BepInExRootPath, new YamlHelper(), new MonoElementFinder());
        _service.Awake();
    }

    internal void Update()
    {
        if (!_enabled)
            return;

        _service.EnsurePatched();

        if (UnityInput.Current.GetKeyDown(_reloadHotkey))
            _service.Reload();

        if (UnityInput.Current.GetKeyDown(_addAllHotKey))
            _service.AddAll();

        var x = UnityInput.Current.mousePosition.x;
        var y = UnityInput.Current.mousePosition.y;
        var z = UnityInput.Current.mousePosition.z;

        if (UnityInput.Current.GetKeyDown(_addAtCursorHotKey))
            _service.AddAtCursor(x, y, z);
    }
}
