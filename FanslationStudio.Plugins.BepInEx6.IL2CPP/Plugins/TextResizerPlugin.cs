using BepInEx;
using BepInEx.Unity.IL2CPP;
using BepInEx.Unity.IL2CPP.Configuration;
using FanslationStudio.Plugins.Shared;
using FanslationStudio.Plugins.SharpYaml;
using FanslationStudio.Plugins.TextResizer;
using HarmonyLib;
using System;
using System.Reflection;
using UnityEngine;

namespace FanslationStudio.Plugins.Plugins;

[BepInPlugin($"{MyPluginInfo.PLUGIN_GUID}.TextResizer", "TextResizer", MyPluginInfo.PLUGIN_VERSION)]
public class TextResizerPlugin : BasePlugin
{
    private static IPluginLogger _logger;
    private static TextResizerService _service;
    private static bool _enabled = true;

    private static readonly KeyboardShortcut AddResizerAtCursorHotKey = new(KeyCode.KeypadDivide);
    private static readonly KeyboardShortcut ReloadHotkey = new(KeyCode.KeypadPlus);
    private static readonly KeyboardShortcut AddResizerHotKey = new(KeyCode.KeypadMultiply);

    public override void Load()
    {
        _enabled = Config.Bind("General",
            "TextResizerEnabled",
            true,
            "Enable Text Resizer plugin").Value;

        _logger = new BepInEx6Logger(base.Log);
        _logger.LogInfo($"[TextResizer DEBUG] Load() called, _enabled={_enabled}");

        if (!_enabled)
            return;

        _service = new TextResizerService(
            _logger, _enabled, Paths.BepInExRootPath, new YamlHelper(), new Il2CppElementFinder());
        _service.Awake();

        TextResizerEditorUi.Configure(_service);

        // BasePlugin (unlike Mono's BaseUnityPlugin) is a plain C# class - Unity never calls
        // Update() on it directly. Every attempt to get a tick via a *generic* Il2Cpp interop
        // call from inside Load() crashes with an AccessViolationException - AddComponent<T>/
        // ClassInjector.RegisterTypeInIl2Cpp<T> (see git history) and delegate conversion
        // (Canvas.willRenderCanvases += (Action)) both reenter Il2CppInterop's generic method
        // resolution (GenericMethod_GetMethod_Hook) while Load() is itself still nested inside
        // the chainloader's own il2cpp_runtime_invoke call, corrupting its state. Harmony
        // patching a concrete, non-generic engine method avoids that code path entirely - it
        // only needs ordinary MethodInfo resolution + an IL detour, no generic instantiation.
        // UnityEngine.Time.deltaTime is read by virtually all game code every frame, so patching
        // its getter gives us a safe once-per-frame tick without any generic interop call.
        var harmony = new Harmony($"{MyPluginInfo.PLUGIN_GUID}.TextResizer");
        var deltaTimeGetter = AccessTools.PropertyGetter(typeof(Time), nameof(Time.deltaTime));
        harmony.Patch(deltaTimeGetter, postfix: new HarmonyMethod(typeof(TextResizerPlugin), nameof(OnDeltaTimeRead)));

        // Diagnostic only: pure .NET reflection over the *actually loaded* runtime types (no
        // Il2Cpp interop invocation at all, so this is always safe) to find out what API surface
        // really exists in this game's unhollowed assemblies, since the Mono-style reference
        // stubs used to compile FanslationStudio.Plugins.Shared can declare overloads that don't
        // actually exist here (see MissingMethodException for GetComponent(Type)/
        // FindObjectsOfType<T>() at runtime despite compiling fine against the stub).
        //LogAvailableMethods(typeof(Component), "GetComponent");
        //LogAvailableMethods(typeof(GameObject), "GetComponent");
        //LogAvailableMethods(typeof(UnityEngine.Object), "FindObjectsOfType");
        //LogAvailableMethods(typeof(UnityEngine.Object), "FindObjectOfType");
    }

    private static void LogAvailableMethods(Type type, string namePrefix)
    {
        foreach (var method in type.GetMethods())
        {
            if (!method.Name.StartsWith(namePrefix, StringComparison.Ordinal))
                continue;

            var parameters = string.Join(", ", Array.ConvertAll(method.GetParameters(), p => p.ParameterType.Name));
            var generics = method.IsGenericMethodDefinition
                ? $"<{string.Join(", ", Array.ConvertAll(method.GetGenericArguments(), g => g.Name))}>"
                : string.Empty;
            _logger.LogInfo($"[TextResizer DEBUG] {type.FullName}.{method.Name}{generics}({parameters})");
        }
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
        try
        {
            if (!_enabled)
                return;

            // TextMetadata/LegacyTextMetadata are stored in plain dictionaries keyed by instance
            // ID rather than attached as custom MonoBehaviour components - see
            // TextMetadataComponents.cs/Il2CppElementFinder.cs. No registration step is needed.
            _service.EnsurePatched();
            TextResizerGameObjectPatches.EnsurePatched();
            _service.CheckForSceneChange();

            if (ReloadHotkey.IsDown())
                _service.Reload();

            if (AddResizerHotKey.IsDown())
            {
                _service.AddResizersForScene();
                TextResizerEditorUi.Open();
            }

            var x = UnityInput.Current.mousePosition.x;
            var y = UnityInput.Current.mousePosition.y;
            var z = UnityInput.Current.mousePosition.z;

            if (AddResizerAtCursorHotKey.IsDown())
            {
                _service.AddResizersAtCursor(x, y, z);
                TextResizerEditorUi.Open();
            }

            TextResizerEditorUi.Tick();
        }
        catch (Exception ex)
        {
            _logger?.LogInfo($"[TextResizer DEBUG] Update() threw: {ex}");
        }
    }
}
