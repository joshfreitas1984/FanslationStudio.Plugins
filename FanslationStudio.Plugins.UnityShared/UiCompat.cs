#if IL2CPP
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
#endif
using UnityEngine;

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
    public static T AddComponent<T>(GameObject gameObject) where T : Component
    {
#if IL2CPP
        return gameObject.AddComponent(Il2CppType.From(typeof(T))).TryCast<T>();
#else
        return gameObject.AddComponent<T>();
#endif
    }

    public static T GetComponent<T>(GameObject gameObject) where T : Component
    {
        if (gameObject == null)
            return null;
#if IL2CPP
        var component = gameObject.GetComponent(Il2CppType.From(typeof(T)));
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
        return As<T>(Object.FindObjectOfType(Il2CppType.From(typeof(T))));
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

    public static Font GetBuiltinFont()
    {
        // Unity 2022.2+ renamed the built-in font; older versions throw for the new name.
        foreach (var name in new[] { "Arial.ttf", "LegacyRuntime.ttf" })
        {
            try
            {
#if IL2CPP
                var font = As<Font>(Resources.GetBuiltinResource(Il2CppType.From(typeof(Font)), name));
#else
                var font = Resources.GetBuiltinResource<Font>(name);
#endif
                if (font != null)
                    return font;
            }
            catch (System.ArgumentException)
            {
            }
        }

        return null;
    }

    /// <summary>Every component on the given GameObject (not its children), by real runtime type -
    /// including custom game MonoBehaviours, unlike the specific-type GetComponent&lt;T&gt; above.</summary>
    public static Component[] GetComponents(GameObject gameObject)
    {
        if (gameObject == null)
            return System.Array.Empty<Component>();
#if IL2CPP
        var components = gameObject.GetComponents(Il2CppType.From(typeof(Component)));
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
}
