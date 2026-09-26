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
