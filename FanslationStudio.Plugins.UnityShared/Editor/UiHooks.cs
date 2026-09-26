using System;
using FanslationStudio.Plugins.Shared;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace FanslationStudio.Plugins.UnityShared.Editor;

/// <summary>
/// Engine hooks shared by the layout and sprite appliers, so each game method is patched once:
///   * Graphic.OnEnable - every Image/Text/TMP as it's enabled or instantiated.
///   * GameObject.SetActive(true) - containers re-shown without any Graphic change.
///   * Image.sprite setter - the game swapping an Image's sprite.
///   * Text.text / TMP_Text.text setter - the game rebinding a label's content, the usual signal
///     that a recycled row (e.g. a list item) just got refreshed with new data.
///
/// Patched lazily from the first tick, never from Load/Awake: under IL2CPP, patching too early
/// can run UI static constructors re-entrantly and crash (see TextResizerService.EnsurePatched).
/// Subscribers are called inside game code, so each one's exceptions are caught and logged once.
/// </summary>
internal static class UiHooks
{
    private static bool _patched;
    private static IPluginLogger _logger;
    private static string _lastError;

    public static event Action<Graphic> GraphicEnabled;
    public static event Action<GameObject> GameObjectActivated;
    public static event Action<Image> ImageSpriteSet;
    public static event Action<Component> TextSet;

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
        Patch(harmony, AccessTools.PropertySetter(typeof(Image), nameof(Image.sprite)), nameof(ImageSpriteSetterPostfix), "Image.sprite");
        Patch(harmony, AccessTools.PropertySetter(typeof(Text), nameof(Text.text)), nameof(TextSetterPostfix), "Text.text");
        Patch(harmony, AccessTools.PropertySetter(typeof(TMP_Text), nameof(TMP_Text.text)), nameof(TmpTextSetterPostfix), "TMP_Text.text");
    }

    private static void Patch(Harmony harmony, System.Reflection.MethodInfo target, string postfix, string label)
    {
        if (target == null)
        {
            _logger?.LogWarning($"[UIEditor] Could not find {label}; rules won't apply automatically there.");
            return;
        }

        try
        {
            harmony.Patch(target, postfix: new HarmonyMethod(typeof(UiHooks), postfix));
        }
        catch (Exception ex)
        {
            _logger?.LogWarning($"[UIEditor] Could not patch {label}: {ex.Message}");
        }
    }

    private static void GraphicOnEnablePostfix(Graphic __instance)
    {
        Raise(GraphicEnabled, __instance);
    }

    private static void SetActivePostfix(GameObject __instance, bool __0)
    {
        if (__0)
            Raise(GameObjectActivated, __instance);
    }

    private static void ImageSpriteSetterPostfix(Image __instance)
    {
        Raise(ImageSpriteSet, __instance);
    }

    private static void TextSetterPostfix(Text __instance)
    {
        Raise(TextSet, __instance);
    }

    private static void TmpTextSetterPostfix(TMP_Text __instance)
    {
        Raise(TextSet, __instance);
    }

    private static void Raise<T>(Action<T> handlers, T argument)
    {
        if (handlers == null)
            return;

        foreach (Action<T> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(argument);
            }
            catch (Exception ex)
            {
                var message = ex.ToString();
                if (message == _lastError)
                    continue;
                _lastError = message;
                _logger?.LogError($"[UIEditor] Hook handler threw: {message}");
            }
        }
    }
}
