using BepInEx;
using BepInEx.Unity.IL2CPP;
using FanslationStudio.Plugins.DynamicStrings;
using FanslationStudio.Plugins.SharpYaml;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using System;
using UnityEngine;

namespace FanslationStudio.Plugins.Plugins;

/// <summary>
/// Used to replace hardcoded strings in IL
/// </summary>
[BepInPlugin($"{MyPluginInfo.PLUGIN_GUID}.DynamicStringPatcher", "DynamicStringPatcher", MyPluginInfo.PLUGIN_VERSION)]
public class StringPatcherPlugin : BasePlugin
{
    public static bool _enabled = false;
    public static StringPatcherService StringPatcherService;

    public override void Load()
    {
        _enabled = Config.Bind("General",
            "Enabled",
            false,
            "Turn plugin to replace dyanmic strings that are hardcoded in code").Value;

        if (!_enabled)
            return;

        var resourcePath = Config.Bind("General", "ResourcePath", "./english",
            "File to dump the dynamic strings to").Value;

        StringPatcherService = new StringPatcherService(new BepInEx6Logger(base.Log),
            _enabled,
            new Harmony($"{MyPluginInfo.PLUGIN_GUID}.DynamicStringPatcher"),
            resourcePath,
            Paths.BepInExRootPath,
            new YamlHelper());

        StringPatcherService.Awake();

        // BasePlugin (unlike Mono's BaseUnityPlugin) is a plain C# class - Unity never calls
        // Update() on it directly. Attach a registered MonoBehaviour component to receive
        // Update() ticks and drive EnsurePatched().
        // if (!ClassInjector.IsTypeRegisteredInIl2Cpp<StringPatcherUpdater>())
        //     ClassInjector.RegisterTypeInIl2Cpp<StringPatcherUpdater>();
        AddComponent<StringPatcherUpdater>();
    }

    internal static void RunUpdate()
    {
        if (!_enabled)
            return;

        StringPatcherService.EnsurePatched();
    }
}

// Actual MonoBehaviour that receives Unity's Update() message under IL2CPP. StringPatcherPlugin
// itself (BasePlugin) is not a Component and never gets ticked by Unity, so this component is
// attached to the scene in Load() to drive the plugin's per-frame logic.
public class StringPatcherUpdater : MonoBehaviour
{
    public StringPatcherUpdater(IntPtr ptr) : base(ptr) { }

    private void Update() => StringPatcherPlugin.RunUpdate();
}