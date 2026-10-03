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
/// Patched lazily from the first tick that has rules to apply, never from Load/Awake: under
/// IL2CPP, patching too early can run UI static constructors re-entrantly and crash (see
/// TextResizerService.EnsurePatched).
/// Subscribers are called inside game code, so each one's exceptions are caught and logged once.
/// </summary>
internal static class UiHooks
{
    private static bool _patched;
    private static bool _textPatched;
    private static Harmony _harmony;
    private static IPluginLogger _logger;
    private static string _lastError;

    // Handlers are kept as arrays, replaced on subscribe, so raising (inside very hot game
    // methods) doesn't allocate the way Delegate.GetInvocationList does.
    private static Action<Graphic>[] _graphicEnabled = Array.Empty<Action<Graphic>>();
    private static Action<GameObject>[] _gameObjectActivated = Array.Empty<Action<GameObject>>();
    private static Action<Image>[] _imageSpriteSet = Array.Empty<Action<Image>>();
    private static Action<Component>[] _textSet = Array.Empty<Action<Component>>();

    public static event Action<Graphic> GraphicEnabled
    {
        add => _graphicEnabled = With(_graphicEnabled, value);
        remove => _graphicEnabled = Without(_graphicEnabled, value);
    }

    public static event Action<GameObject> GameObjectActivated
    {
        add => _gameObjectActivated = With(_gameObjectActivated, value);
        remove => _gameObjectActivated = Without(_gameObjectActivated, value);
    }

    public static event Action<Image> ImageSpriteSet
    {
        add => _imageSpriteSet = With(_imageSpriteSet, value);
        remove => _imageSpriteSet = Without(_imageSpriteSet, value);
    }

    public static event Action<Component> TextSet
    {
        add => _textSet = With(_textSet, value);
        remove => _textSet = Without(_textSet, value);
    }

    /// <summary>
    /// Patches the hooks that aren't patched yet. Callers only ask once there are rules to apply:
    /// every call to a patched method pays for the hook (under IL2CPP a native-to-managed
    /// transition, plus marshalling the whole string for the text setters) whether or not any
    /// rule exists. The text setters only feed layout rules, so they're patched separately.
    /// </summary>
    /// <returns>True if anything was newly patched, so the caller can apply rules to elements
    /// that were enabled before the hooks existed.</returns>
    public static bool EnsurePatched(string harmonyId, IPluginLogger logger, bool includeTextSetters)
    {
        if (_patched && (_textPatched || !includeTextSetters))
            return false;

        _logger = logger;
        _harmony ??= new Harmony(harmonyId);

        if (!_patched)
        {
            _patched = true;
            Patch(AccessTools.Method(typeof(Graphic), "OnEnable"), null, nameof(GraphicOnEnablePostfix), "Graphic.OnEnable");
            Patch(AccessTools.Method(typeof(GameObject), nameof(GameObject.SetActive), new[] { typeof(bool) }),
                null, nameof(SetActivePostfix), "GameObject.SetActive");
            Patch(AccessTools.PropertySetter(typeof(Image), nameof(Image.sprite)), null, nameof(ImageSpriteSetterPostfix), "Image.sprite");
        }

        if (includeTextSetters && !_textPatched)
        {
            _textPatched = true;
            Patch(AccessTools.PropertySetter(typeof(Text), nameof(Text.text)), null, nameof(TextSetterPostfix), "Text.text");
            Patch(AccessTools.PropertySetter(typeof(TMP_Text), nameof(TMP_Text.text)), null, nameof(TmpTextSetterPostfix), "TMP_Text.text");
        }

        return true;
    }

    private static void Patch(System.Reflection.MethodInfo target, string prefix, string postfix, string label)
    {
        if (target == null)
        {
            _logger?.LogWarning($"[UIEditor] Could not find {label}; rules won't apply automatically there.");
            return;
        }

        try
        {
            _harmony.Patch(target,
                prefix: prefix == null ? null : new HarmonyMethod(typeof(UiHooks), prefix),
                postfix: new HarmonyMethod(typeof(UiHooks), postfix));
        }
        catch (Exception ex)
        {
            _logger?.LogWarning($"[UIEditor] Could not patch {label}: {ex.Message}");
        }
    }

    private static void GraphicOnEnablePostfix(Graphic __instance)
    {
        Raise(_graphicEnabled, __instance);
    }

    // Objects raised by the SetActive postfix this frame, by instance ID.
    private static readonly System.Collections.Generic.HashSet<int> _activatedThisFrame = new System.Collections.Generic.HashSet<int>();
    private static int _activatedFrame = -1;

    // Raised for every SetActive(true), even on an already-active object: games often
    // instantiate a panel, reparent it, then "show" it with SetActive(true), and that call is
    // the only signal that its rules now match. Some games also call SetActive(true) every
    // frame, so each object is raised at most once per frame.
    private static void SetActivePostfix(GameObject __instance, bool __0)
    {
        if (!__0 || _gameObjectActivated.Length == 0)
            return;

        var frame = Time.frameCount;
        if (frame != _activatedFrame)
        {
            _activatedFrame = frame;
            _activatedThisFrame.Clear();
        }

        if (_activatedThisFrame.Add(__instance.GetInstanceID()))
            Raise(_gameObjectActivated, __instance);
    }

    private static void ImageSpriteSetterPostfix(Image __instance)
    {
        Raise(_imageSpriteSet, __instance);
    }

    private static void TextSetterPostfix(Text __instance)
    {
        Raise(_textSet, __instance);
    }

    private static void TmpTextSetterPostfix(TMP_Text __instance)
    {
        Raise(_textSet, __instance);
    }

    private static Action<T>[] With<T>(Action<T>[] handlers, Action<T> handler)
    {
        if (handler == null)
            return handlers;

        var result = new Action<T>[handlers.Length + 1];
        Array.Copy(handlers, result, handlers.Length);
        result[handlers.Length] = handler;
        return result;
    }

    private static Action<T>[] Without<T>(Action<T>[] handlers, Action<T> handler)
    {
        var index = Array.IndexOf(handlers, handler);
        if (index < 0)
            return handlers;

        var result = new Action<T>[handlers.Length - 1];
        Array.Copy(handlers, 0, result, 0, index);
        Array.Copy(handlers, index + 1, result, index, handlers.Length - index - 1);
        return result;
    }

    private static void Raise<T>(Action<T>[] handlers, T argument)
    {
        for (var i = 0; i < handlers.Length; i++)
        {
            try
            {
                handlers[i](argument);
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
