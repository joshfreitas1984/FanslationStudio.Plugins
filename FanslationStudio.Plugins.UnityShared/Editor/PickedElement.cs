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
    Text = TmpText | LegacyText,
}

/// <summary>A UI element found under the cursor, with what the editor tabs can do with it.</summary>
internal sealed class PickedElement
{
    public RectTransform RectTransform { get; private set; }
    public string Path { get; private set; }
    public ElementCapabilities Capabilities { get; private set; }
    public string TextPreview { get; private set; }

    public bool IsAlive => RectTransform != null;
    public string Name => RectTransform != null ? RectTransform.name : "(destroyed)";

    public static PickedElement From(RectTransform rectTransform)
    {
        var element = new PickedElement
        {
            RectTransform = rectTransform,
            Path = ObjectHelper.GetGameObjectPath(rectTransform.gameObject),
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

        return element;
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
