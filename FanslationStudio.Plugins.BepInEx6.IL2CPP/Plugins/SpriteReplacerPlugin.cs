using BepInEx;
using BepInEx.Unity.IL2CPP;
using BepInEx.Unity.IL2CPP.Configuration;
using FanslationStudio.Plugins.Shared;
using FanslationStudio.Plugins.SharpYaml;
using FanslationStudio.Plugins.Sprites;
using Il2CppInterop.Runtime.Injection;
using System;
using UnityEngine;

namespace FanslationStudio.Plugins.Plugins;

[BepInPlugin($"{MyPluginInfo.PLUGIN_GUID}.SpriteReplacer", "SpriteReplacer", MyPluginInfo.PLUGIN_VERSION)]
public class SpriteReplacerPlugin : BasePlugin
{
    private static IPluginLogger _logger;
    private static SpriteReplacerService _service;
    private static readonly KeyboardShortcut AddAtCursorHotKey = new(KeyCode.F1);
    private static readonly KeyboardShortcut AddAllHotKey = new(KeyCode.F2);
    private static readonly KeyboardShortcut ReloadHotkey = new(KeyCode.F3);
    private static bool _enabled;

    public override void Load()
    {
        _enabled = Config.Bind("General",
            "Enabled",
            false,
            "Turn on sprite replacer plugin").Value;

        if (!_enabled)
            return;

        _logger = new BepInEx6Logger(base.Log);
        _service = new SpriteReplacerService(
            _logger, _enabled, Paths.BepInExRootPath, new YamlHelper(), new Il2CppElementFinder());

        _service.Awake();

        // BasePlugin (unlike Mono's BaseUnityPlugin) is a plain C# class - Unity never calls
        // Update() on it directly. Attach a registered MonoBehaviour component to receive
        // Update() ticks and drive the hotkey polling.
        if (!ClassInjector.IsTypeRegisteredInIl2Cpp<SpriteReplacerUpdater>())
            ClassInjector.RegisterTypeInIl2Cpp<SpriteReplacerUpdater>();
        AddComponent<SpriteReplacerUpdater>();
    }

    internal static void RunUpdate()
    {
        if (!_enabled)
            return;

        _service.EnsurePatched();
        SpriteReplacerGameObjectPatches.EnsurePatched();

        if (ReloadHotkey.IsDown())
            _service.Reload();

        if (AddAllHotKey.IsDown())
            _service.AddAll();

        var x = UnityInput.Current.mousePosition.x;
        var y = UnityInput.Current.mousePosition.y;
        var z = UnityInput.Current.mousePosition.z;

        if (AddAtCursorHotKey.IsDown())
            _service.AddAtCursor(x, y, z);
    }
}

// Actual MonoBehaviour that receives Unity's Update() message under IL2CPP. SpriteReplacerPlugin
// itself (BasePlugin) is not a Component and never gets ticked by Unity, so this component is
// attached to the scene in Load() to drive the plugin's per-frame logic.
public class SpriteReplacerUpdater : MonoBehaviour
{
    public SpriteReplacerUpdater(IntPtr ptr) : base(ptr) { }

    private void Update() => SpriteReplacerPlugin.RunUpdate();
}
