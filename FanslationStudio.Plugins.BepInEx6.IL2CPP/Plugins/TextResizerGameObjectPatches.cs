using System;
using System.Collections.Generic;
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

    // Il2CppType.From results, resolved on first use (never from a static initializer or Load).
    private static Il2CppSystem.Type _tmpIl2CppType;
    private static Il2CppSystem.Type _textIl2CppType;

    private static bool HasResizers => TextResizerService.ResizersLoaded && TextResizerService.Resizers.Count > 0;

    // Applies resizers to every TMP/legacy text in the subtree. Inactive children are skipped:
    // their texts aren't enabled, and they can only become active through their own
    // SetActive(true), which runs this walk (and the OnEnable hooks) for them then.
    private static void ApplyToSubtree(Transform root)
    {
        _tmpIl2CppType ??= Il2CppType.From(typeof(TextMeshProUGUI));
        _textIl2CppType ??= Il2CppType.From(typeof(Text));

        ForEachComponentInChildren(root, typeof(TextMeshProUGUI), _tmpIl2CppType, ApplyTmp);
        ForEachComponentInChildren(root, typeof(Text), _textIl2CppType, ApplyLegacy);
    }

    private static readonly Action<Component> ApplyTmp = c => TextResizerService.ApplyResizing((TextMeshProUGUI)c, refreshPath: true);
    private static readonly Action<Component> ApplyLegacy = c => TextResizerService.ApplyResizingToLegacyText((Text)c, refreshPath: true);

    // GetComponent(Il2CppType.From(componentType)) has been observed to occasionally return a
    // Component that is not actually an instance of the requested type (InvalidCastException at
    // the call site), suggesting the Il2Cpp-type-filtered overload isn't reliably enforcing the
    // type filter in this build. Guard with a managed type check before invoking the callback so
    // a mismatched component is skipped instead of crashing.
    private static void ForEachComponentInChildren(Transform transform, Type componentType, Il2CppSystem.Type il2CppType, Action<Component> action)
    {
        var component = transform.GetComponent(il2CppType);
        if (component != null && componentType.IsInstanceOfType(component))
            action(component);

        for (var i = 0; i < transform.childCount; i++)
        {
            var child = transform.GetChild(i);
            if (child.gameObject.activeSelf)
                ForEachComponentInChildren(child, componentType, il2CppType, action);
        }
    }

    // Objects walked by the SetActive postfix this frame, by instance ID.
    private static readonly HashSet<int> _walkedThisFrame = [];
    private static int _walkedFrame = -1;

    // Walks on every SetActive(true), even for an already-active object: games commonly
    // instantiate a panel (its texts' OnEnable then sees the prefab's root path, matching no
    // rule), reparent it under the Canvas, then "show" it with SetActive(true) - and this walk,
    // with fresh paths, is what applies the resizers. Some games also call SetActive(true) every
    // frame, so each object is walked at most once per frame.
    [HarmonyPostfix, HarmonyPatch(typeof(GameObject), nameof(GameObject.SetActive), [typeof(bool)])]
    public static void Postfix_GameObject_SetActive(GameObject __instance, bool value)
    {
        if (!value || !HasResizers)
            return;

        var frame = Time.frameCount;
        if (frame != _walkedFrame)
        {
            _walkedFrame = frame;
            _walkedThisFrame.Clear();
        }

        if (!_walkedThisFrame.Add(__instance.GetInstanceID()))
            return;

        ApplyToSubtree(__instance.transform);
    }

    // Fades set alpha every frame; only the step from invisible (<= 0) to visible needs a walk.
    [HarmonyPrefix, HarmonyPatch(typeof(CanvasGroup), "alpha", MethodType.Setter)]
    public static void Prefix_CanvasGroup_SetAlpha(CanvasGroup __instance, out bool __state)
    {
        __state = HasResizers && __instance.alpha <= 0;
    }

    [HarmonyPostfix, HarmonyPatch(typeof(CanvasGroup), "alpha", MethodType.Setter)]
    public static void Postfix_CanvasGroup_SetAlpha(CanvasGroup __instance, float value, bool __state)
    {
        // Only apply when canvas is being made visible (alpha going above 0)
        if (__state && value > 0)
            ApplyToSubtree(__instance.transform);
    }
}
