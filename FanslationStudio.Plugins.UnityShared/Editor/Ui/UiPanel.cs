using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace FanslationStudio.Plugins.UnityShared.Editor.Ui;

/// <summary>
/// A rebuildable region of the editor window plus the widgets in it.
///
/// Interaction is polled rather than event-driven: buttons are hit-tested against the mouse
/// position and the focused input is diffed against its last-seen text every frame. UnityEvent
/// listeners (Button.onClick, InputField.onValueChanged) are avoided on purpose - subscribing
/// interop delegates is a crash risk under IL2CPP (see .github/copilot-instructions.md).
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

    // Bumped by Clear, so callers and caches can tell a handler rebuilt the panel under them.
    private int _version;
    // The focused input, found once per frame (isFocused is an interop call per field).
    private TrackedInput _focused;
    private int _focusFrame = -1;
    private int _focusVersion = -1;
    // The input focused at the last poll, so its final text is read on the frame focus leaves it.
    private TrackedInput _lastFocused;

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
    public bool AnyInputFocused => FocusedInput() != null;

    private TrackedInput FocusedInput()
    {
        var frame = Time.frameCount;
        if (frame == _focusFrame && _focusVersion == _version)
            return _focused;

        _focusFrame = frame;
        _focusVersion = _version;
        _focused = null;
        for (var i = 0; i < _inputs.Count; i++)
        {
            var field = _inputs[i].Field;
            if (field != null && field.isFocused)
            {
                _focused = _inputs[i];
                break;
            }
        }
        return _focused;
    }

    public void Clear()
    {
        for (var i = Rect.childCount - 1; i >= 0; i--)
        {
            // Destroy only takes effect at the end of the frame; deactivate first so the old
            // widgets stop drawing (and dirtying the canvas batch) this frame.
            var child = Rect.GetChild(i).gameObject;
            child.SetActive(false);
            UnityEngine.Object.Destroy(child);
        }
        _clickables.Clear();
        _inputs.Clear();
        _focused = null;
        _lastFocused = null;
        _version++;
    }

    /// <summary>Runs the click handler under the pointer, if any. Returns true if one ran.</summary>
    public bool HandleClick(Vector2 screenPoint)
    {
        // Stops at the first handler, so a handler that rebuilds this panel is fine.
        for (var i = 0; i < _clickables.Count; i++)
        {
            var clickable = _clickables[i];
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

    /// <summary>
    /// Fires the change handler of the focused input, and of the one that just lost focus (so a
    /// last keystroke, paste or Escape revert on the frame it was left is still seen). Only typing
    /// changes a field and code-driven changes go through <see cref="SetInputText"/>, so the
    /// unfocused fields' text (a string marshal each under IL2CPP) is never read.
    /// </summary>
    public void PollInputs()
    {
        var focused = FocusedInput();
        var previous = _lastFocused;
        _lastFocused = focused;

        var version = _version;
        if (previous != null && previous != focused)
        {
            Poll(previous);
            if (version != _version)
                return; // the handler rebuilt the panel
        }

        if (focused != null)
            Poll(focused);
    }

    private static void Poll(TrackedInput input)
    {
        if (input.Field == null)
            return;

        var text = input.Field.text;
        if (text == input.LastText)
            return;

        input.LastText = text;
        input.OnChanged?.Invoke(text);
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

    /// <summary>Shows a sprite, letterboxed to fit.</summary>
    public Image Picture(Sprite sprite, float x, float y, float width, float height)
    {
        var go = new GameObject("Picture");
        go.transform.SetParent(Rect, false);
        var image = UiCompat.AddComponent<Image>(go);
        image.sprite = sprite;
        image.preserveAspect = true;
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

    /// <summary>
    /// A clickable button. The returned handle changes its label and colour in place, so toggles
    /// don't have to rebuild the whole panel.
    /// </summary>
    public UiButton Button(string text, float x, float y, float width, float height, Action onClick, Color? color = null)
    {
        var go = new GameObject("Button");
        go.transform.SetParent(Rect, false);
        var image = UiCompat.AddComponent<Image>(go);
        var buttonColor = color ?? ButtonColor;
        image.color = buttonColor;
        // Clicks are hit-tested geometrically (HandleClick), not raycast. The window background
        // behind the button is still a raycast target, so clicks never reach the game.
        image.raycastTarget = false;
        var rect = UiCompat.GetRectTransform(go);
        PlaceTopLeft(rect, x, y, width, height);

        var button = new UiButton(rect, image, width, buttonColor);
        button.SetText(text);

        if (onClick != null)
            _clickables.Add(new Clickable { Rect = rect, OnClick = onClick });
        return button;
    }

    internal static Text CreateButtonLabel(RectTransform parent)
    {
        var labelGo = new GameObject("Label");
        labelGo.transform.SetParent(parent, false);
        var label = UiCompat.AddComponent<Text>(labelGo);
        label.font = UiCompat.GetBuiltinFont();
        label.fontSize = 13;
        label.color = Color.white;
        label.alignment = TextAnchor.MiddleCenter;
        label.horizontalOverflow = HorizontalWrapMode.Overflow;
        label.verticalOverflow = VerticalWrapMode.Overflow;
        label.raycastTarget = false;

        var labelRect = UiCompat.GetRectTransform(labelGo);
        labelRect.anchorMin = new Vector2(0.5f, 0f);
        labelRect.anchorMax = new Vector2(0.5f, 1f);
        labelRect.pivot = new Vector2(0.5f, 0.5f);
        labelRect.anchoredPosition = Vector2.zero;
        return label;
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

/// <summary>A button made by <see cref="UiPanel.Button"/>; its label and colour can change in place.</summary>
internal sealed class UiButton
{
    private readonly float _width;
    private Text _label;
    private RectTransform _labelRect;
    private string _text;
    private Color _color;

    public RectTransform Rect { get; }
    public Image Image { get; }

    internal UiButton(RectTransform rect, Image image, float width, Color color)
    {
        Rect = rect;
        Image = image;
        _width = width;
        _color = color;
    }

    public void Set(string text, Color color)
    {
        SetText(text);
        SetColor(color);
    }

    public void SetColor(Color color)
    {
        if (color == _color)
            return;
        _color = color;
        Image.color = color;
    }

    public void SetText(string text)
    {
        text ??= string.Empty;
        if (_text == text)
            return;
        _text = text;

        // List rows are blank buttons with separate labels on top: no label object for those.
        if (_label == null)
        {
            if (text.Length == 0)
                return;
            _label = UiPanel.CreateButtonLabel(Rect);
            _labelRect = UiCompat.GetRectTransform(_label);
        }

        _label.text = text;
        // MiddleCenter doesn't reliably centre against a full-width rect on some hosts (labels
        // hug the left edge), so size the label to its own text and centre that instead.
        _labelRect.sizeDelta = new Vector2(text.Length == 0 ? 0f : Mathf.Min(_label.preferredWidth + 8f, _width), 0f);
    }
}
