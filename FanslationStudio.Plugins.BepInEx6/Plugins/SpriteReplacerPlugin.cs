using BepInEx;
using BepInEx.Unity.Mono;
using FanslationStudio.Plugins.Shared;
using FanslationStudio.Plugins.SharpYaml;
using FanslationStudio.Plugins.Sprites;
using UnityEngine;

namespace FanslationStudio.Plugins.Plugins;

// Mono (BepInEx6) compiles directly against the real UnityEngine assemblies, so calling the
// generic FindObjectsOfType<T>() here is safe - unlike Shared, which is compiled against the
// Mono-style stub assemblies.
public class MonoSpriteElementFinder : ISpriteElementFinder
{
    public UnityEngine.UI.Image[] FindAllElements() => UnityEngine.Object.FindObjectsOfType<UnityEngine.UI.Image>();
}

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
            _logger, _enabled, Paths.BepInExRootPath, new YamlHelper(), new MonoSpriteElementFinder());

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
