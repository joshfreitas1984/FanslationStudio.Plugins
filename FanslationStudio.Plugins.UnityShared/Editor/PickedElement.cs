using System;
using FanslationStudio.Plugins.Support;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace FanslationStudio.Plugins.UnityShared.Editor;

[Flags]
internal enum ElementCapabilities
{
    None = 0,
    TmpText = 1,
    LegacyText = 2,
    Sprite = 4,
    SizeFitter = 8,
    LayoutGroup = 16,
    Text = TmpText | LegacyText,
}

/// <summary>A UI element found under the cursor, with what the editor tabs can do with it.</summary>
internal sealed class PickedElement
{
    public RectTransform RectTransform { get; private set; }
    public string Path { get; private set; }
    public ElementCapabilities Capabilities { get; private set; }
    public string TextPreview { get; private set; }

    /// <summary>Real runtime type name of every component on this GameObject, comma-separated -
    /// the quickest way to spot a custom game script (not a stock Unity component) driving an
    /// element's layout, sizing or behaviour.</summary>
    public string ComponentList { get; private set; }

    /// <summary>True if a CanvasGroup fades this element out (alpha ~0); still pickable, just not visible.</summary>
    public bool IsHidden { get; private set; }

    public bool IsAlive => RectTransform != null;
    public string Name => RectTransform != null ? RectTransform.name : "(destroyed)";

    public static PickedElement From(RectTransform rectTransform, bool isHidden = false)
    {
        var element = new PickedElement
        {
            RectTransform = rectTransform,
            Path = ObjectHelper.GetGameObjectPath(rectTransform.gameObject),
            IsHidden = isHidden,
        };

        element.DetectTmp();

        var legacyText = UiCompat.GetComponent<Text>(rectTransform);
        if (legacyText != null)
        {
            element.Capabilities |= ElementCapabilities.LegacyText;
            element.TextPreview ??= legacyText.text;
        }

        var image = UiCompat.GetComponent<Image>(rectTransform);
        if (image != null && image.sprite != null)
            element.Capabilities |= ElementCapabilities.Sprite;

        if (UiCompat.GetComponent<ContentSizeFitter>(rectTransform) != null)
            element.Capabilities |= ElementCapabilities.SizeFitter;
        if (UiCompat.GetComponent<LayoutGroup>(rectTransform) != null)
            element.Capabilities |= ElementCapabilities.LayoutGroup;

        element.ComponentList = BuildComponentList(rectTransform);

        return element;
    }

    private static string BuildComponentList(RectTransform rectTransform)
    {
        var components = UiCompat.GetComponents(rectTransform.gameObject);
        var names = new string[components.Length];
        for (var i = 0; i < components.Length; i++)
            names[i] = ComponentTypeName(components[i]);
        return string.Join(", ", names);
    }

    // Under IL2CPP, every unhollowed component is exposed through the same wrapper type
    // (Component), so C#'s GetType().Name always says "Component" - the real class only shows up
    // via GetIl2CppType(), which reflects the underlying native class instead.
    internal static string ComponentTypeName(Component component)
    {
        if (component == null)
            return "(null)";
#if IL2CPP
        return component.GetIl2CppType().Name;
#else
        return component.GetType().Name;
#endif
    }

    // TMP is looked up in its own method so a game without Unity.TextMeshPro fails only this call
    // (TypeLoadException when it is JIT-compiled) instead of the whole picker.
    private void DetectTmp()
    {
        try
        {
            DetectTmpCore();
        }
        catch (TypeLoadException)
        {
        }
        catch (System.IO.FileNotFoundException)
        {
        }
    }

    private void DetectTmpCore()
    {
        var tmp = UiCompat.GetComponent<TMP_Text>(RectTransform);
        if (tmp == null)
            return;

        Capabilities |= ElementCapabilities.TmpText;
        TextPreview = tmp.text;
    }

    public string CapabilityTags
    {
        get
        {
            var tags = string.Empty;
            if ((Capabilities & ElementCapabilities.Text) != 0)
                tags += "[Text]";
            if ((Capabilities & ElementCapabilities.Sprite) != 0)
                tags += "[Sprite]";
            if ((Capabilities & ElementCapabilities.SizeFitter) != 0)
                tags += "[Fitter]";
            if ((Capabilities & ElementCapabilities.LayoutGroup) != 0)
                tags += "[LayoutGrp]";
            if (IsHidden)
                tags += "[Hidden]";
            return tags + "[Layout]";
        }
    }

    public override string ToString()
    {
        var preview = string.IsNullOrEmpty(TextPreview) ? string.Empty : $" \"{Truncate(TextPreview.Replace('\n', ' '), 30)}\"";
        return $"{Name} {CapabilityTags}{preview}";
    }

    internal static string Truncate(string value, int maxLength)
    {
        if (value == null || value.Length <= maxLength)
            return value;
        return value.Substring(0, maxLength - 1) + "…";
    }
}
