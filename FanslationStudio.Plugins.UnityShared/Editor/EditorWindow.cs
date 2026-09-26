using System.Collections.Generic;
using BepInEx;
using FanslationStudio.Plugins.UnityShared.Editor.Tabs;
using FanslationStudio.Plugins.UnityShared.Editor.Ui;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace FanslationStudio.Plugins.UnityShared.Editor;

/// <summary>
/// The UI Editor window: the picked element stack on the left, and tabs (Layout, Text, Sprite)
/// for the selected element on the right. Built from plain uGUI at runtime and driven by
/// polling (see <see cref="UiPanel"/>), so it works the same on Mono and IL2CPP.
/// </summary>
internal static class EditorWindow
{
    private const float WindowWidth = 840f;
    private const float WindowHeight = 650f;
    private const float Padding = 12f;
    private const float TitleHeight = 30f;
    private const float ListWidth = 240f;
    private const float HeaderHeight = 72f;
    private const float StatusHeight = 22f;
    private const string PrefKeyX = "FSUIEditor.WindowX";
    private const string PrefKeyY = "FSUIEditor.WindowY";

    private static readonly List<IEditorTab> _tabs = new List<IEditorTab>
    {
        new LayoutTab(),
        new PlaceholderTab("Text", "Text resizer editing moves here next."),
        new PlaceholderTab("Sprite", "Sprite dump/replace moves here next."),
    };

    private static float _scale = 1f;
    private static GameObject _root;
    private static RectTransform _window;
    private static UiPanel _titleBar;
    private static RectTransform _dragHandle;
    private static UiPanel _list;
    private static UiPanel _header;
    private static UiPanel _content;
    private static Text _status;

    private static bool _selectionDirty = true;
    private static int _tabIndex;
    private static IEditorTab _builtTab;
    private static RectTransform _builtElement;

    private static bool _dragging;
    private static Vector2 _dragPointerStart;
    private static Vector2 _dragWindowStart;

    public static bool IsOpen => _root != null && _root.activeSelf;

    /// <summary>True while the user is typing in a field, so global hotkeys should stand down.</summary>
    public static bool IsTyping => IsOpen && ((_content != null && _content.AnyInputFocused) || (_header != null && _header.AnyInputFocused));

    public static void Configure(float scale)
    {
        _scale = Mathf.Clamp(scale, 0.5f, 3f);
        PickerController.SelectionChanged += _ => _selectionDirty = true;
    }

    public static void Toggle()
    {
        if (IsOpen) Close(); else Open();
    }

    public static void Open()
    {
        EnsureBuilt();
        _root.SetActive(true);
        _selectionDirty = true;
    }

    public static void Close()
    {
        if (_root == null)
            return;

        LeaveTab();
        _root.SetActive(false);
        _dragging = false;
    }

    public static void SetStatus(string text, bool warning = false)
    {
        if (_status == null)
            return;
        _status.text = text ?? string.Empty;
        _status.color = warning ? UiPanel.WarningColor : UiPanel.DimTextColor;
    }

    public static void Tick()
    {
        if (!IsOpen)
            return;

        if (_selectionDirty)
        {
            _selectionDirty = false;
            RebuildList();
            RebuildElement();
        }

        var input = UnityInput.Current;
        var mouse = (Vector2)input.mousePosition;

        PollDrag(input, mouse);

        if (!_dragging && input.GetMouseButtonDown(0))
        {
            // Topmost regions first; stop at the first handler.
            if (!_titleBar.HandleClick(mouse) && !_header.HandleClick(mouse) && !_content.HandleClick(mouse))
                _list.HandleClick(mouse);
        }

        _header.PollInputs();
        _content.PollInputs();
        _builtTab?.Tick();
    }

    private static void RebuildList()
    {
        _list.Clear();
        var width = _list.Width;
        _list.Label("Under cursor", 0, 0, width, 20, 13, TextAnchor.MiddleLeft, UiPanel.DimTextColor, FontStyle.Bold);

        var stack = PickerController.Stack;
        var selected = PickerController.Selected;
        const float rowHeight = 22f;
        const float buttonsHeight = 64f;
        var maxRows = Mathf.Max(1, (int)((_list.Rect.rect.height - 26f - buttonsHeight) / (rowHeight + 2f)));

        var y = 26f;
        if (stack.Count == 0)
        {
            _list.Label($"Press {UiEditorHotkeys.Describe(PickerController.Hotkeys.Pick)} over a UI element.", 0, y, width, 40, 12,
                TextAnchor.UpperLeft, UiPanel.DimTextColor);
        }

        // Keep the selected row in view.
        var first = Mathf.Clamp(PickerController.StackIndex - maxRows / 2, 0, Mathf.Max(0, stack.Count - maxRows));
        for (var i = first; i < stack.Count && i < first + maxRows; i++)
        {
            var element = stack[i];
            var index = i;
            var isSelected = selected != null && element.RectTransform == selected.RectTransform;
            _list.Button(string.Empty, 0, y, width, rowHeight, () => PickerController.SelectStackIndex(index),
                isSelected ? UiPanel.SelectedColor : UiPanel.MutedButtonColor);
            _list.Label($"{i + 1}. {PickedElement.Truncate(element.Name, 22)} {Tags(element)}", 6, y, width - 8, rowHeight, 12);
            y += rowHeight + 2f;
        }

        if (stack.Count > first + maxRows)
            _list.Label($"+{stack.Count - first - maxRows} more ({CycleText()})", 0, y, width, 18, 11,
                TextAnchor.MiddleLeft, UiPanel.DimTextColor);

        var buttonsY = _list.Rect.rect.height - buttonsHeight + 6f;
        var half = (width - 4f) / 2f;
        _list.Button("Parent", 0, buttonsY, half, 24, PickerController.SelectParent, UiPanel.MutedButtonColor);
        _list.Button("Child", half + 4f, buttonsY, half, 24, PickerController.SelectChild, UiPanel.MutedButtonColor);
        _list.Button("Clear selection", 0, buttonsY + 30f, width, 24, PickerController.Clear, UiPanel.MutedButtonColor);
    }

    private static string CycleText()
    {
        var hotkeys = PickerController.Hotkeys;
        var keys = $"{UiEditorHotkeys.Describe(hotkeys.Previous)} / {UiEditorHotkeys.Describe(hotkeys.Next)}";
        return hotkeys.WheelText != null ? $"{hotkeys.WheelText} or {keys}" : keys;
    }

    private static string Tags(PickedElement element)
    {
        var tags = string.Empty;
        if ((element.Capabilities & ElementCapabilities.Text) != 0) tags += "T";
        if ((element.Capabilities & ElementCapabilities.Sprite) != 0) tags += "S";
        return tags.Length > 0 ? $"[{tags}]" : string.Empty;
    }

    private static void RebuildElement()
    {
        var element = PickerController.Selected;

        // Re-selecting the element that's already open (e.g. re-picking the same spot) keeps
        // the tab as-is, so in-progress edits aren't thrown away.
        if (element != null && _builtTab != null && element.RectTransform == _builtElement)
        {
            RebuildHeader(element);
            return;
        }

        LeaveTab();
        RebuildHeader(element);
        _content.Clear();

        if (element == null)
        {
            _content.Label($"Press {UiEditorHotkeys.Describe(PickerController.Hotkeys.Pick)} over a UI element to pick it, " +
                $"then use {CycleText()} or the list to choose.",
                0, 0, _content.Width, 60, 13, TextAnchor.UpperLeft, UiPanel.DimTextColor);
            return;
        }

        var tab = _tabs[_tabIndex];
        tab.Build(_content, element);
        _builtTab = tab;
        _builtElement = element.RectTransform;
    }

    private static void RebuildHeader(PickedElement element)
    {
        _header.Clear();
        if (element == null)
            return;

        var width = _header.Width;
        _header.Label($"{element.Name}  {element.CapabilityTags}", 0, 0, width, 20, 14, TextAnchor.MiddleLeft, null, FontStyle.Bold);
        _header.Label(element.Path, 0, 20, width, 18, 11, TextAnchor.MiddleLeft, UiPanel.DimTextColor);

        if (!_tabs[_tabIndex].IsAvailable(element))
            _tabIndex = FirstAvailableTab(element);

        var x = 0f;
        for (var i = 0; i < _tabs.Count; i++)
        {
            var tab = _tabs[i];
            var index = i;
            var available = tab.IsAvailable(element);
            var color = i == _tabIndex ? UiPanel.SelectedColor : available ? UiPanel.MutedButtonColor : new Color(0.18f, 0.18f, 0.2f, 1f);
            _header.Button(tab.Title, x, 44, 100, 26, available ? () => SwitchTab(index) : (System.Action)null, color);
            x += 104f;
        }
    }

    private static int FirstAvailableTab(PickedElement element)
    {
        for (var i = 0; i < _tabs.Count; i++)
        {
            if (_tabs[i].IsAvailable(element))
                return i;
        }
        return 0;
    }

    private static void SwitchTab(int index)
    {
        if (index == _tabIndex && _builtTab != null)
            return;

        LeaveTab();
        _tabIndex = index;
        _builtElement = null;
        RebuildElement();
    }

    private static void LeaveTab()
    {
        _builtTab?.Leave();
        _builtTab = null;
        _builtElement = null;
    }

    private static void PollDrag(IInputSystem input, Vector2 mouse)
    {
        if (!_dragging)
        {
            if (input.GetMouseButtonDown(0) && RectTransformUtility.RectangleContainsScreenPoint(_dragHandle, mouse, null))
            {
                _dragging = true;
                _dragPointerStart = mouse;
                _dragWindowStart = _window.anchoredPosition;
            }
            return;
        }

        if (!input.GetMouseButton(0))
        {
            _dragging = false;
            PlayerPrefs.SetFloat(PrefKeyX, _window.anchoredPosition.x);
            PlayerPrefs.SetFloat(PrefKeyY, _window.anchoredPosition.y);
            PlayerPrefs.Save();
            return;
        }

        _window.anchoredPosition = _dragWindowStart + (mouse - _dragPointerStart) / _scale;
    }

    private static void EnsureBuilt()
    {
        if (_root != null)
            return;

        _root = new GameObject(ElementPicker.EditorObjectPrefix + "Window");
        Object.DontDestroyOnLoad(_root);

        var canvas = UiCompat.AddComponent<Canvas>(_root);
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = short.MaxValue;
        var scaler = UiCompat.AddComponent<CanvasScaler>(_root);
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
        scaler.scaleFactor = _scale;
        UiCompat.AddComponent<GraphicRaycaster>(_root);
        EnsureEventSystem();

        var windowPanel = UiPanel.Create(UiCompat.GetRectTransform(_root), "Window", 0, 0, WindowWidth, WindowHeight,
            new Color(0.08f, 0.08f, 0.1f, 0.96f));
        _window = windowPanel.Rect;
        _window.anchorMin = _window.anchorMax = new Vector2(0.5f, 0.5f);
        _window.pivot = new Vector2(0.5f, 0.5f);
        _window.anchoredPosition = new Vector2(PlayerPrefs.GetFloat(PrefKeyX, 0f), PlayerPrefs.GetFloat(PrefKeyY, 0f));

        _titleBar = UiPanel.Create(_window, "TitleBar", 0, 0, WindowWidth, TitleHeight, new Color(0.16f, 0.16f, 0.2f, 1f));
        _titleBar.Label("UI Editor", Padding, 0, 300, TitleHeight, 14, TextAnchor.MiddleLeft, null, FontStyle.Bold);
        _titleBar.Label($"{UiEditorHotkeys.Describe(PickerController.Hotkeys.ToggleWindow)}: show/hide", 320, 0, 300, TitleHeight, 11,
            TextAnchor.MiddleLeft, UiPanel.DimTextColor);
        _titleBar.Button("X", WindowWidth - 34, 3, 28, TitleHeight - 6, Close, UiPanel.DangerColor);
        _dragHandle = UiPanel.Create(_titleBar.Rect, "DragHandle", 0, 0, WindowWidth - 40, TitleHeight).Rect;

        var bodyTop = TitleHeight + Padding;
        var bodyHeight = WindowHeight - bodyTop - StatusHeight - Padding;
        _list = UiPanel.Create(_window, "List", Padding, bodyTop, ListWidth, bodyHeight);

        var rightX = Padding * 2 + ListWidth;
        var rightWidth = WindowWidth - rightX - Padding;
        _header = UiPanel.Create(_window, "Header", rightX, bodyTop, rightWidth, HeaderHeight);
        _content = UiPanel.Create(_window, "Content", rightX, bodyTop + HeaderHeight + 8f, rightWidth,
            bodyHeight - HeaderHeight - 8f);

        var statusPanel = UiPanel.Create(_window, "Status", Padding, WindowHeight - StatusHeight - 6f, WindowWidth - 2 * Padding, StatusHeight);
        _status = statusPanel.Label(string.Empty, 0, 0, WindowWidth - 2 * Padding, StatusHeight, 12, TextAnchor.MiddleLeft, UiPanel.DimTextColor);
    }

    private static void EnsureEventSystem()
    {
        if (UiCompat.FindObjectOfType<EventSystem>() != null)
            return;

        // Needed for InputField focus and typing. Only created if the game has none.
        var go = new GameObject(ElementPicker.EditorObjectPrefix + "EventSystem");
        Object.DontDestroyOnLoad(go);
        UiCompat.AddComponent<EventSystem>(go);
        UiCompat.AddComponent<StandaloneInputModule>(go);
    }

    private sealed class PlaceholderTab : IEditorTab
    {
        private readonly string _message;

        public PlaceholderTab(string title, string message)
        {
            Title = title;
            _message = message;
        }

        public string Title { get; }
        public bool IsAvailable(PickedElement element) => false;
        public void Build(UiPanel panel, PickedElement element) =>
            panel.Label(_message, 0, 0, panel.Width, 40, 13, TextAnchor.UpperLeft, UiPanel.DimTextColor);
        public void Tick() { }
        public void Leave() { }
    }
}
