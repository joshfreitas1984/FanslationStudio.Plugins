#if IL2CPP
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
#endif
using FanslationStudio.Plugins.Support;
using UnityEngine;
using UnityEngine.EventSystems;

namespace FanslationStudio.Plugins.UnityShared;

/// <summary>
/// Hides the Mono vs IL2CPP interop differences so UnityShared code can be written once.
///
/// IL2CPP rules this wraps (see .github/copilot-instructions.md):
///   * Use non-generic AddComponent/GetComponent with Il2CppType.From(...) and TryCast the result.
///   * Never use `as`/`is` on interop objects - use TryCast.
///   * Out-buffer arrays (GetWorldCorners) must be Il2CppStructArray, or the results are lost.
/// </summary>
internal static class UiCompat
{
#if IL2CPP
    // Il2CppType.From(Type) resolves the native class through reflection (MakeGenericType +
    // GetField) and allocates a new wrapper on every call, and the calls below run per element on
    // hot hooks. Cached per CLR type; non-generic on purpose (see hazard 3 in the instructions).
    private static readonly System.Collections.Generic.Dictionary<System.Type, Il2CppSystem.Type> Il2CppTypes = new();

    public static Il2CppSystem.Type Il2CppTypeOf(System.Type type)
    {
        if (!Il2CppTypes.TryGetValue(type, out var il2CppType))
        {
            il2CppType = Il2CppType.From(type);
            Il2CppTypes[type] = il2CppType;
        }

        return il2CppType;
    }
#endif

    public static T AddComponent<T>(GameObject gameObject) where T : Component
    {
#if IL2CPP
        return gameObject.AddComponent(Il2CppTypeOf(typeof(T))).TryCast<T>();
#else
        return gameObject.AddComponent<T>();
#endif
    }

    public static T GetComponent<T>(GameObject gameObject) where T : Component
    {
        if (gameObject == null)
            return null;
#if IL2CPP
        var component = gameObject.GetComponent(Il2CppTypeOf(typeof(T)));
        return component == null ? null : component.TryCast<T>();
#else
        return gameObject.GetComponent<T>();
#endif
    }

    public static T GetComponent<T>(Component component) where T : Component
    {
        return component == null ? null : GetComponent<T>(component.gameObject);
    }

    public static T As<T>(Object obj) where T : Object
    {
        if (obj == null)
            return null;
#if IL2CPP
        return obj.TryCast<T>();
#else
        return obj as T;
#endif
    }

    public static RectTransform GetRectTransform(Component component)
    {
        return component == null ? null : As<RectTransform>(component.transform);
    }

    public static RectTransform GetRectTransform(GameObject gameObject)
    {
        return gameObject == null ? null : As<RectTransform>(gameObject.transform);
    }

    public static T FindObjectOfType<T>() where T : Object
    {
#if IL2CPP
        return As<T>(Object.FindObjectOfType(Il2CppTypeOf(typeof(T))));
#else
        return Object.FindObjectOfType<T>();
#endif
    }

    public static T[] FindObjectsOfType<T>() where T : Object
    {
        // Generic FindObjectsOfType<T> is fine here: this file is compiled against each host's
        // real Unity assemblies (see Il2CppElementFinder.FindAllTextElements).
        return Object.FindObjectsOfType<T>();
    }

    public static T GetComponentInParent<T>(Component component) where T : Component
    {
        for (var current = component == null ? null : component.transform; current != null; current = current.parent)
        {
            var found = GetComponent<T>(current.gameObject);
            if (found != null)
                return found;
        }

        return null;
    }

    private static Font _builtinFont;
    private static bool _builtinFontLookedUp;

    /// <summary>The built-in UI font, looked up once (every editor label asks for it).</summary>
    public static Font GetBuiltinFont()
    {
        if (_builtinFontLookedUp)
            return _builtinFont;

        _builtinFontLookedUp = true;
        // Unity 2022.2+ renamed the built-in font; older versions throw for the new name. Under
        // IL2CPP the failure surfaces as an interop exception, not ArgumentException, so catch all.
        foreach (var name in new[] { "Arial.ttf", "LegacyRuntime.ttf" })
        {
            try
            {
#if IL2CPP
                var font = As<Font>(Resources.GetBuiltinResource(Il2CppTypeOf(typeof(Font)), name));
#else
                var font = Resources.GetBuiltinResource<Font>(name);
#endif
                if (font != null)
                {
                    _builtinFont = font;
                    break;
                }
            }
            catch (System.Exception)
            {
            }
        }

        return _builtinFont;
    }

    /// <summary>Every component on the given GameObject (not its children), by real runtime type -
    /// including custom game MonoBehaviours, unlike the specific-type GetComponent&lt;T&gt; above.</summary>
    public static Component[] GetComponents(GameObject gameObject)
    {
        if (gameObject == null)
            return System.Array.Empty<Component>();
#if IL2CPP
        var components = gameObject.GetComponents(Il2CppTypeOf(typeof(Component)));
        var result = new Component[components.Length];
        for (var i = 0; i < components.Length; i++)
            result[i] = components[i] as Component;
        return result;
#else
        return gameObject.GetComponents(typeof(Component));
#endif
    }

    public static Vector3[] GetWorldCorners(RectTransform rectTransform)
    {
#if IL2CPP
        var corners = new Il2CppStructArray<Vector3>(4);
        rectTransform.GetWorldCorners(corners);
        return corners;
#else
        var corners = new Vector3[4];
        rectTransform.GetWorldCorners(corners);
        return corners;
#endif
    }

#if IL2CPP
    private static Il2CppStructArray<Vector3> _cornersBuffer;
#endif

    /// <summary>
    /// Fills <paramref name="result"/> (length 4 or more) with the world corners without
    /// allocating, for callers that run every frame.
    /// </summary>
    public static void GetWorldCorners(RectTransform rectTransform, Vector3[] result)
    {
#if IL2CPP
        // Must be an Il2CppStructArray (hazard 7), so keep one native buffer and copy out of it.
        _cornersBuffer ??= new Il2CppStructArray<Vector3>(4);
        rectTransform.GetWorldCorners(_cornersBuffer);
        for (var i = 0; i < 4; i++)
            result[i] = _cornersBuffer[i];
#else
        rectTransform.GetWorldCorners(result);
#endif
    }

    /// <summary>
    /// Makes sure an EventSystem exists so InputField focus and typing work. Only created if the game
    /// has none; the objects are named with the editor prefix so pickers and appliers skip them.
    /// </summary>
    public static void EnsureEventSystem()
    {
        if (FindObjectOfType<EventSystem>() != null)
            return;

        var go = new GameObject(ObjectHelper.EditorObjectPrefix + "EventSystem");
        Object.DontDestroyOnLoad(go);
        AddComponent<EventSystem>(go);
        AddComponent<StandaloneInputModule>(go);
    }
}
