using System;
using FanslationStudio.Plugins.Shared;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace FanslationStudio.Plugins.UnityShared.Layout;

/// <summary>
/// Applies layout rules as UI appears, without per-frame scanning:
///   * Graphic.OnEnable - fires for every Image/Text/TMP as it's enabled or instantiated; the
///     applier walks up to ancestors, so rules on plain containers apply too.
///   * GameObject.SetActive(true) - covers containers re-shown without any Graphic change.
///
/// Patched lazily from the first tick, never from Load/Awake: under IL2CPP, patching too early
/// can run UI static constructors re-entrantly and crash (see TextResizerService.EnsurePatched).
/// Every postfix swallows its exceptions - they would otherwise surface inside game code.
/// </summary>
internal static class LayoutHooks
{
    private static bool _patched;
    private static IPluginLogger _logger;
    private static string _lastError;

    public static void EnsurePatched(string harmonyId, IPluginLogger logger)
    {
        if (_patched)
            return;
        _patched = true;
        _logger = logger;

        var harmony = new Harmony(harmonyId);
        Patch(harmony, AccessTools.Method(typeof(Graphic), "OnEnable"), nameof(GraphicOnEnablePostfix), "Graphic.OnEnable");
        Patch(harmony, AccessTools.Method(typeof(GameObject), nameof(GameObject.SetActive), new[] { typeof(bool) }),
            nameof(SetActivePostfix), "GameObject.SetActive");
    }

    private static void Patch(Harmony harmony, System.Reflection.MethodInfo target, string postfix, string label)
    {
        if (target == null)
        {
            _logger?.LogWarning($"[UIEditor] Could not find {label}; layouts won't apply automatically there.");
            return;
        }

        try
        {
            harmony.Patch(target, postfix: new HarmonyMethod(typeof(LayoutHooks), postfix));
        }
        catch (Exception ex)
        {
            _logger?.LogWarning($"[UIEditor] Could not patch {label}: {ex.Message}");
        }
    }

    private static void GraphicOnEnablePostfix(Graphic __instance)
    {
        if (!LayoutApplier.HasRules)
            return;

        try
        {
            LayoutApplier.OnElementEnabled(__instance.transform);
        }
        catch (Exception ex)
        {
            LogOnce(ex);
        }
    }

    private static void SetActivePostfix(GameObject __instance, bool __0)
    {
        if (!__0 || !LayoutApplier.HasRules)
            return;

        try
        {
            LayoutApplier.OnElementEnabled(__instance.transform);
        }
        catch (Exception ex)
        {
            LogOnce(ex);
        }
    }

    private static void LogOnce(Exception ex)
    {
        var message = ex.ToString();
        if (message == _lastError)
            return;
        _lastError = message;
        _logger?.LogError($"[UIEditor] Layout hook threw: {message}");
    }
}
