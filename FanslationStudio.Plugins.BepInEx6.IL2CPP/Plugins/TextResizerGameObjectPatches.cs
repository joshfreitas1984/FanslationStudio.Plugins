using System;
using FanslationStudio.Plugins.TextResizer;
using HarmonyLib;
using Il2CppInterop.Runtime;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace FanslationStudio.Plugins.Plugins;

// GameObject.SetActive/CanvasGroup.alpha postfixes that need to walk a hierarchy and call
// Component.GetComponent(Type). These previously lived in TextResizerService.cs (in
// FanslationStudio.Plugins.Shared) and threw MissingMethodException at runtime, even though the
// method genuinely exists in the game's real assembly - Shared is compiled against the Mono-style
// stub assemblies in Reference\, which are physically different from the game's real unhollowed
// assemblies, and Il2CppInterop can't resolve calls made from IL compiled against the stub. This
// class must stay in the BepInEx6.IL2CPP host project, compiled directly against unhollowed\, for
// any of its Unity API calls to resolve correctly. See .github/copilot-instructions.md item 4.
public static class TextResizerGameObjectPatches
{
    private static bool _patched;

    /// <summary>
    /// Applies these patches. Must be called after at least one frame/scene has run (e.g. from
    /// the plugin's Update, not from Awake/Load) for the same reason as TextResizerService.EnsurePatched.
    /// </summary>
    public static void EnsurePatched()
    {
        if (_patched)
            return;

        Harmony.CreateAndPatchAll(typeof(TextResizerGameObjectPatches), $"{MyPluginInfo.PLUGIN_GUID}.TextResizer.GameObjectPatches");
        _patched = true;
    }

    // GetComponent(Il2CppType.From(componentType)) has been observed to occasionally return a
    // Component that is not actually an instance of the requested type (InvalidCastException at
    // the call site), suggesting the Il2Cpp-type-filtered overload isn't reliably enforcing the
    // type filter in this build. Guard with a managed type check before invoking the callback so
    // a mismatched component is skipped instead of crashing.
    private static void ForEachComponentInChildren(Transform transform, Type componentType, Action<Component> action)
    {
        var component = transform.GetComponent(Il2CppType.From(componentType));
        if (component != null && componentType.IsInstanceOfType(component))
            action(component);

        for (var i = 0; i < transform.childCount; i++)
            ForEachComponentInChildren(transform.GetChild(i), componentType, action);
    }

    [HarmonyPostfix, HarmonyPatch(typeof(GameObject), nameof(GameObject.SetActive), [typeof(bool)])]
    public static void Postfix_GameObject_SetActive(GameObject __instance, bool value)
    {
        if (!TextResizerService.ResizersLoaded || !value)
            return;

        ForEachComponentInChildren(__instance.transform, typeof(TextMeshProUGUI), c => TextResizerService.ApplyResizing((TextMeshProUGUI)c));
        ForEachComponentInChildren(__instance.transform, typeof(Text), c => TextResizerService.ApplyResizingToLegacyText((Text)c));
    }

    [HarmonyPostfix, HarmonyPatch(typeof(CanvasGroup), "alpha", MethodType.Setter)]
    public static void Postfix_CanvasGroup_SetAlpha(CanvasGroup __instance, float value)
    {
        if (!TextResizerService.ResizersLoaded)
            return;

        // Only apply when canvas is being made visible (alpha going above 0)
        if (value > 0)
        {
            ForEachComponentInChildren(__instance.transform, typeof(TextMeshProUGUI), c => TextResizerService.ApplyResizing((TextMeshProUGUI)c));
            ForEachComponentInChildren(__instance.transform, typeof(Text), c => TextResizerService.ApplyResizingToLegacyText((Text)c));
        }
    }
}
