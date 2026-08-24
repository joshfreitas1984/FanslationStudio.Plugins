using BepInEx;
using BepInEx.Unity.IL2CPP;
using BepInEx.Unity.IL2CPP.Configuration;
using FanslationStudio.Plugins.DynamicStrings;
using FanslationStudio.Plugins.PrefabText;
using FanslationStudio.Plugins.Shared;
using HarmonyLib;
using UnityEngine;

namespace FanslationStudio.Plugins.Plugins;


// We need to wrap any services we want to use in the plugin itself,
// because BepInEx doesn't allow us to use typed references to the service in the plugin,
// and we don't want to use reflection everywhere.
public class PrefabTextDumperServiceWrapper : PrefabTextDumperService
{
    public PrefabTextDumperServiceWrapper(IPluginLogger logger, string dumpFilePath, string regexPattern, bool enabled, string gameDataPath, string bepinExPath, IPrefabTextFinder elementFinder)
        : base(logger, dumpFilePath, regexPattern, enabled, gameDataPath, bepinExPath, elementFinder)
    {
    }
}

/// <summary>
/// Used to replace hardcoded strings in IL
/// </summary>
[BepInPlugin($"{MyPluginInfo.PLUGIN_GUID}.PrefabTextDumperPlugin", "PrefabTextDumperPlugin", MyPluginInfo.PLUGIN_VERSION)]
public class PrefabTextDumperPlugin : BasePlugin
{
    private static IPluginLogger _logger;
    private static PrefabTextDumperServiceWrapper _service;
    private static readonly KeyboardShortcut DumpHotkey = new(KeyCode.Keypad4);

    public override void Load()
    {
        var enabled = Config.Bind("General", "Enabled", false,
            "Enable dynamic string dumping on startup").Value;
        var regexPattern = Config.Bind("General", "ForeignLanguagePattern", DynamicStringSupport.ChineseCharPattern,
            "Regex pattern for foreign language to scan for").Value;
        var dumpFiles = Config.Bind("General", "DumpFilePath", "./dumpeddata",
            "File to dump the dynamic strings to").Value;

        _logger = new BepInEx6Logger(base.Log);
        _service = new PrefabTextDumperServiceWrapper(_logger, dumpFiles, regexPattern, enabled,
            Paths.GameDataPath, Paths.BepInExRootPath, new Il2CppElementFinder());
        _service.Awake();

        if (!enabled)
            return;

        // BasePlugin (unlike Mono's BaseUnityPlugin) is a plain C# class - Unity never calls
        // Update() on it directly. Every attempt to get a tick via a *generic* Il2Cpp interop
        // call from inside Load() (ClassInjector.RegisterTypeInIl2Cpp<T>()/AddComponent<T>())
        // crashes with an AccessViolationException - it reenters Il2CppInterop's generic method
        // resolution (GenericMethod_GetMethod_Hook) while Load() is itself still nested inside
        // the chainloader's own il2cpp_runtime_invoke call, corrupting its state. Harmony
        // patching a concrete, non-generic engine method avoids that code path entirely - it
        // only needs ordinary MethodInfo resolution + an IL detour, no generic instantiation.
        // See TextResizerPlugin for the same pattern.
        var harmony = new Harmony($"{MyPluginInfo.PLUGIN_GUID}.PrefabTextDumperPlugin");
        var deltaTimeGetter = AccessTools.PropertyGetter(typeof(Time), nameof(Time.deltaTime));
        harmony.Patch(deltaTimeGetter, postfix: new HarmonyMethod(typeof(PrefabTextDumperPlugin), nameof(OnDeltaTimeRead)));
    }

    private static int _lastTickedFrame = -1;

    private static void OnDeltaTimeRead()
    {
        // Time.deltaTime can be read many times per frame - only tick once per frame.
        var frame = Time.frameCount;
        if (frame == _lastTickedFrame)
            return;

        _lastTickedFrame = frame;
        RunUpdate();
    }

    internal static void RunUpdate()
    {
        if (_service == null)
            return;

        if (DumpHotkey.IsDown())
            _service.DumpAllPrefabTexts();
    }
}
