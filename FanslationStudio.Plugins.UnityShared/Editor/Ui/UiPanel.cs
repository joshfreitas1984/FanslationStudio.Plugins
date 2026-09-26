using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace FanslationStudio.Plugins.UnityShared.Editor.Ui;

/// <summary>
/// A rebuildable region of the editor window plus the widgets in it.
///
/// Interaction is polled rather than event-driven: buttons are hit-tested against the mouse
/// position and inputs are diffed against their last-seen text every frame. UnityEvent
/// listeners (Button.onClick, InputField.onValueChanged) are avoided on purpose - subscribing
/// interop delegates is a crash risk under IL2CPP (see TextResizerEditorUi).
///
/// Coordinates are (x, y) from the panel's top-left, y growing downwards.
/// </summary>
internal sealed class UiPanel
{
    public static readonly Color TextColor = new Color(0.92f, 0.92f, 0.92f, 1f);
    public static readonly Color DimTextColor = new Color(0.6f, 0.6f, 0.6f, 1f);
    public static readonly Color WarningColor = new Color(1f, 0.75f, 0.3f, 1f);
    public static readonly Color ButtonColor = new Color(0.25f, 0.45f, 0.85f, 1f);
    public static readonly Color MutedButtonColor = new Color(0.28f, 0.28f, 0.32f, 1f);
    public static readonly Color SelectedColor = new Color(0.55f, 0.25f, 0.5f, 1f);
    public static readonly Color DangerColor = new Color(0.75f, 0.25f, 0.25f, 1f);

    private sealed class Clickable
    {
        public RectTransform Rect;
        public Action OnClick;
    }

    private sealed class TrackedInput
    {
        public InputField Field;
        public string LastText;
        public Action<string> OnChanged;
    }

    private readonly List<Clickable> _clickables = new List<Clickable>();
    private readonly List<TrackedInput> _inputs = new List<TrackedInput>();

    public RectTransform Rect { get; }

    private UiPanel(RectTransform rect)
    {
        Rect = rect;
    }

    public static UiPanel Create(RectTransform parent, string name, float x, float y, float width, float height, Color? background = null)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        RectTransform rect;
        if (background.HasValue)
        {
            var image = UiCompat.AddComponent<Image>(go);
            image.color = background.Value;
            rect = UiCompat.GetRectTransform(go);
        }
        else
        {
            rect = UiCompat.AddComponent<RectTransform>(go);
        }

        PlaceTopLeft(rect, x, y, width, height);
        return new UiPanel(rect);
    }

    public float Width => Rect.rect.width;

    /// <summary>True while the user is typing in one of this panel's inputs.</summary>
    public bool AnyInputFocused
    {
        get
        {
            foreach (var input in _inputs)
            {
                if (input.Field != null && input.Field.isFocused)
                    return true;
            }
            return false;
        }
    }

    public void Clear()
    {
        for (var i = Rect.childCount - 1; i >= 0; i--)
            UnityEngine.Object.Destroy(Rect.GetChild(i).gameObject);
        _clickables.Clear();
        _inputs.Clear();
    }

    /// <summary>Runs the click handler under the pointer, if any. Returns true if one ran.</summary>
    public bool HandleClick(Vector2 screenPoint)
    {
        // Copy: a handler may rebuild this panel.
        foreach (var clickable in _clickables.ToArray())
        {
            if (clickable.Rect != null && clickable.Rect.gameObject.activeInHierarchy
                && RectTransformUtility.RectangleContainsScreenPoint(clickable.Rect, screenPoint, null))
            {
                clickable.OnClick();
                return true;
            }
        }
        return false;
    }

    public bool Contains(Vector2 screenPoint)
    {
        return RectTransformUtility.RectangleContainsScreenPoint(Rect, screenPoint, null);
    }

    public void PollInputs()
    {
        foreach (var input in _inputs.ToArray())
        {
            if (input.Field == null)
                continue;

            var text = input.Field.text;
            if (text == input.LastText)
                continue;

            input.LastText = text;
            input.OnChanged?.Invoke(text);
        }
    }

    /// <summary>Sets an input's text without firing its change handler.</summary>
    public void SetInputText(InputField field, string text)
    {
        foreach (var input in _inputs)
        {
            if (input.Field == field)
            {
                input.LastText = text ?? string.Empty;
                break;
            }
        }
        field.text = text ?? string.Empty;
    }

    public Image Box(float x, float y, float width, float height, Color color)
    {
        var go = new GameObject("Box");
        go.transform.SetParent(Rect, false);
        var image = UiCompat.AddComponent<Image>(go);
        image.color = color;
        image.raycastTarget = false;
        PlaceTopLeft(UiCompat.GetRectTransform(go), x, y, width, height);
        return image;
    }

    public Text Label(string text, float x, float y, float width, float height, int fontSize = 13,
        TextAnchor alignment = TextAnchor.MiddleLeft, Color? color = null, FontStyle style = FontStyle.Normal)
    {
        var go = new GameObject("Label");
        go.transform.SetParent(Rect, false);
        var label = UiCompat.AddComponent<Text>(go);
        label.font = UiCompat.GetBuiltinFont();
        label.fontSize = fontSize;
        label.fontStyle = style;
        label.color = color ?? TextColor;
        label.alignment = alignment;
        label.horizontalOverflow = HorizontalWrapMode.Wrap;
        label.verticalOverflow = VerticalWrapMode.Truncate;
        label.raycastTarget = false;
        label.text = text ?? string.Empty;
        PlaceTopLeft(UiCompat.GetRectTransform(go), x, y, width, height);
        return label;
    }

    public RectTransform Button(string text, float x, float y, float width, float height, Action onClick, Color? color = null)
    {
        var go = new GameObject("Button");
        go.transform.SetParent(Rect, false);
        var image = UiCompat.AddComponent<Image>(go);
        image.color = color ?? ButtonColor;
        var rect = UiCompat.GetRectTransform(go);
        PlaceTopLeft(rect, x, y, width, height);

        var labelGo = new GameObject("Label");
        labelGo.transform.SetParent(rect, false);
        var label = UiCompat.AddComponent<Text>(labelGo);
        label.font = UiCompat.GetBuiltinFont();
        label.fontSize = 13;
        label.color = Color.white;
        label.alignment = TextAnchor.MiddleCenter;
        label.horizontalOverflow = HorizontalWrapMode.Overflow;
        label.verticalOverflow = VerticalWrapMode.Overflow;
        label.raycastTarget = false;
        label.text = text;

        // MiddleCenter doesn't reliably centre against a full-width rect on some hosts (labels
        // hug the left edge), so size the label to its own text and centre that instead.
        var labelRect = UiCompat.GetRectTransform(labelGo);
        labelRect.anchorMin = new Vector2(0.5f, 0f);
        labelRect.anchorMax = new Vector2(0.5f, 1f);
        labelRect.pivot = new Vector2(0.5f, 0.5f);
        labelRect.sizeDelta = new Vector2(Mathf.Min(label.preferredWidth + 8f, width), 0f);
        labelRect.anchoredPosition = Vector2.zero;

        if (onClick != null)
            _clickables.Add(new Clickable { Rect = rect, OnClick = onClick });
        return rect;
    }

    public InputField Input(string text, float x, float y, float width, float height, Action<string> onChanged, string placeholder = null)
    {
        var go = new GameObject("Input");
        go.transform.SetParent(Rect, false);
        var image = UiCompat.AddComponent<Image>(go);
        image.color = new Color(1f, 1f, 1f, 0.92f);
        PlaceTopLeft(UiCompat.GetRectTransform(go), x, y, width, height);

        var textComponent = CreateInputText(go.transform, "Text", Color.black, FontStyle.Normal);

        Text placeholderComponent = null;
        if (!string.IsNullOrEmpty(placeholder))
        {
            placeholderComponent = CreateInputText(go.transform, "Placeholder", new Color(0.45f, 0.45f, 0.45f, 1f), FontStyle.Italic);
            placeholderComponent.text = placeholder;
        }

        var field = UiCompat.AddComponent<InputField>(go);
        field.textComponent = textComponent;
        if (placeholderComponent != null)
            field.placeholder = placeholderComponent;
        field.text = text ?? string.Empty;

        _inputs.Add(new TrackedInput { Field = field, LastText = field.text, OnChanged = onChanged });
        return field;
    }

    private static Text CreateInputText(Transform parent, string name, Color color, FontStyle style)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        var text = UiCompat.AddComponent<Text>(go);
        text.font = UiCompat.GetBuiltinFont();
        text.fontSize = 13;
        text.fontStyle = style;
        text.color = color;
        text.alignment = TextAnchor.MiddleLeft;
        text.horizontalOverflow = HorizontalWrapMode.Overflow;
        text.supportRichText = false;
        text.raycastTarget = false;
        var rect = UiCompat.GetRectTransform(go);
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = new Vector2(5f, 1f);
        rect.offsetMax = new Vector2(-5f, -1f);
        return text;
    }

    internal static void PlaceTopLeft(RectTransform rect, float x, float y, float width, float height)
    {
        rect.anchorMin = new Vector2(0f, 1f);
        rect.anchorMax = new Vector2(0f, 1f);
        rect.pivot = new Vector2(0f, 1f);
        rect.sizeDelta = new Vector2(width, height);
        rect.anchoredPosition = new Vector2(x, -y);
    }
}
