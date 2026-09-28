using System;
using System.Collections.Generic;
using BepInEx;
using FanslationStudio.Plugins.Support;
using FanslationStudio.Plugins.UnityShared.Editor.Tabs;
using FanslationStudio.Plugins.UnityShared.Editor.Ui;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace FanslationStudio.Plugins.UnityShared.Editor;

/// <summary>
/// The UI Editor window. The left column lists either the elements under the cursor or every
/// saved rule of the current tab's kind; the right side shows tabs (Layout, Text, Sprite) for
/// the selected element or rule. Built from plain uGUI at runtime and driven by polling (see
/// <see cref="UiPanel"/>), so it works the same on Mono and IL2CPP.
/// </summary>
internal static class EditorWindow
{
    private enum ListMode
    {
        UnderCursor,
        Rules,
    }

    private const float WindowWidth = 840f;
    private const float WindowHeight = 760f;
    private const float Padding = 12f;
    private const float TitleHeight = 30f;
    private const float ListWidth = 240f;
    private const float ListTopHeight = 58f;
    private const float HeaderHeight = 90f;
    private const float StatusHeight = 22f;
    private const string PrefKeyX = "FSUIEditor.WindowX";
    private const string PrefKeyY = "FSUIEditor.WindowY";

    private static readonly List<IEditorTab> _tabs = new List<IEditorTab>
    {
        new LayoutTab(),
        new TextTab(),
        new SpriteTab(),
        new InspectTab(),
    };

    private static float _scale = 1f;
    private static GameObject _root;
    private static RectTransform _window;
    private static UiPanel _titleBar;
    private static RectTransform _dragHandle;
    private static UiPanel _listTop;
    private static UiPanel _listBody;
    private static UiPanel _header;
    private static UiPanel _content;
    private static UiPanel _popup;
    private static UiPanel _settings;
    private static Text _status;

    private static ListMode _listMode = ListMode.UnderCursor;
    private static string _ruleFilter = string.Empty;
    private static int _rulePage;
    private static string _rulesListKey;

    private static bool _selectionDirty = true;
    private static int _tabIndex;
    private static IEditorTab _builtTab;
    private static RectTransform _builtElement;
    private static string _builtRulePath;

    // Set when a rule is chosen in the Rules list; consumed by the next RebuildElement.
    private static string _openRulePath;

    private static bool _dragging;
    private static Vector2 _dragPointerStart;
    private static Vector2 _dragWindowStart;

    public static bool IsOpen => _root != null && _root.activeSelf;

    private static bool SettingsOpen => _settings != null && _settings.Rect.gameObject.activeSelf;

    /// <summary>True while the user is typing in a field, so global hotkeys should stand down.</summary>
    public static bool IsTyping => IsOpen && (
        (_content != null && _content.AnyInputFocused) ||
        (_header != null && _header.AnyInputFocused) ||
        (_listTop != null && _listTop.AnyInputFocused) ||
        (SettingsOpen && _settings.AnyInputFocused));

    public static void Configure(float scale)
    {
        _scale = Mathf.Clamp(scale, 0.5f, 3f);
        PickerController.SelectionChanged += _ => _selectionDirty = true;
        PickerController.Picked += () =>
        {
            // A fresh pick is about what's under the cursor.
            CloseSettings();
            if (_listMode != ListMode.UnderCursor)
                SetListMode(ListMode.UnderCursor);
        };
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
        CloseSettings();
        _root.SetActive(false);
        _dragging = false;
    }

    /// <summary>Selects an arbitrary element (e.g. one found by drilling into the Inspect tab's
    /// child browser) and switches to the Layout tab so its rule can be created/edited right away,
    /// rather than requiring the element to already be the top-level picked one.</summary>
    public static void SelectForEditing(RectTransform rectTransform)
    {
        if (rectTransform == null)
            return;

        // LeaveTab() first, same as SwitchTab(): RebuildElement()'s "re-picking the same spot
        // keeps the tab as-is" shortcut keys only off the element, not the tab index, so without
        // this it would rebuild the header but leave the OLD tab's content on screen.
        LeaveTab();
        PickerController.SelectElement(rectTransform);
        var layoutTabIndex = _tabs.FindIndex(t => t is LayoutTab);
        _tabIndex = layoutTabIndex >= 0 ? layoutTabIndex : 0;
        _rulePage = 0;
        _selectionDirty = true;
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
            RebuildElement();
            RebuildListBody();
        }
        else if (_listMode == ListMode.Rules && RulesListIsStale())
        {
            RebuildListBody();
        }

        var input = UnityInput.Current;
        var mouse = (Vector2)input.mousePosition;

        PollDrag(input, mouse);

        if (SettingsOpen)
        {
            // The settings view covers the whole body, so only it and the title bar take input.
            if (!_dragging && input.GetMouseButtonDown(0) && !_titleBar.HandleClick(mouse))
                _settings.HandleClick(mouse);
            _settings.PollInputs();
            return;
        }

        if (!_dragging && input.GetMouseButtonDown(0))
        {
            if (_popup != null)
            {
                // A click outside an open popup just closes it.
                if (!_popup.HandleClick(mouse) && !_popup.Contains(mouse))
                    CloseChoice();
            }
            // Topmost regions first; stop at the first handler.
            else if (!_titleBar.HandleClick(mouse) && !_header.HandleClick(mouse) && !_content.HandleClick(mouse)
                     && !_listTop.HandleClick(mouse))
            {
                _listBody.HandleClick(mouse);
            }
        }

        _listTop.PollInputs();
        _header.PollInputs();
        _content.PollInputs();
        _builtTab?.Tick();
    }

    /// <summary>
    /// Shows a grid of options over the tab content. "" is shown as "(game default)", meaning
    /// the field is left unset.
    /// </summary>
    public static void ShowChoice(string title, IList<string> options, string current, Action<string> onPick)
    {
        CloseChoice();

        var content = _content.Rect;
        var width = content.rect.width;
        var height = content.rect.height;
        // Same place as the content panel (PlaceTopLeft stores top-left offsets as (x, -y)).
        _popup = UiPanel.Create(_window, "Popup", content.anchoredPosition.x, -content.anchoredPosition.y, width, height,
            new Color(0.12f, 0.12f, 0.16f, 0.99f));

        _popup.Label(title, 8, 4, width - 110, 22, 13, TextAnchor.MiddleLeft, null, FontStyle.Bold);
        _popup.Button("Cancel", width - 90, 4, 82, 22, CloseChoice, UiPanel.MutedButtonColor);

        const int columns = 3;
        const float rowHeight = 22f;
        const float gap = 4f;
        var columnWidth = (width - 16f - (columns - 1) * gap) / columns;
        var rows = Mathf.Max(1, (int)((height - 40f) / (rowHeight + 2f)));
        var shown = Mathf.Min(options.Count, rows * columns);

        for (var i = 0; i < shown; i++)
        {
            var option = options[i];
            var x = 8f + (i / rows) * (columnWidth + gap);
            var y = 34f + (i % rows) * (rowHeight + 2f);
            var label = string.IsNullOrEmpty(option) ? "(game default)" : option;
            _popup.Button(label, x, y, columnWidth, rowHeight, () =>
            {
                CloseChoice();
                onPick(option);
            }, (option ?? string.Empty) == (current ?? string.Empty) ? UiPanel.SelectedColor : UiPanel.MutedButtonColor);
        }
    }

    private static void CloseChoice()
    {
        if (_popup == null)
            return;
        Object.Destroy(_popup.Rect.gameObject);
        _popup = null;
    }

    // ---- Plugin settings ---------------------------------------------------------------------

    private static void ToggleSettings()
    {
        if (SettingsOpen)
        {
            CloseSettings();
            return;
        }

        // Leave the tab first so auto-save runs before the settings cover it.
        LeaveTab();
        _selectionDirty = true;
        _settings.Clear();
        _settings.Rect.gameObject.SetActive(true);
        SettingsView.Build(_settings, CloseSettings);
    }

    private static void CloseSettings()
    {
        if (!SettingsOpen)
            return;
        _settings.Clear();
        _settings.Rect.gameObject.SetActive(false);
    }

    // ---- Left column -------------------------------------------------------------------------

    private static void SetListMode(ListMode mode)
    {
        _listMode = mode;
        RebuildListTop();
        RebuildListBody();
    }

    private static void RebuildListTop()
    {
        _listTop.Clear();
        var width = _listTop.Width;
        var half = (width - 4f) / 2f;

        _listTop.Button("Under cursor", 0, 0, half, 24, () => SetListMode(ListMode.UnderCursor),
            _listMode == ListMode.UnderCursor ? UiPanel.SelectedColor : UiPanel.MutedButtonColor);
        _listTop.Button("Rules", half + 4f, 0, half, 24, () => SetListMode(ListMode.Rules),
            _listMode == ListMode.Rules ? UiPanel.SelectedColor : UiPanel.MutedButtonColor);

        if (_listMode == ListMode.Rules)
        {
            _listTop.Input(_ruleFilter, 0, 30, width, 24, text =>
            {
                _ruleFilter = text ?? string.Empty;
                _rulePage = 0;
                RebuildListBody();
            }, "search path or description");
        }
        else
        {
            _listTop.Label($"{UiEditorHotkeys.Describe(PickerController.Hotkeys.Pick)} to pick", 0, 30, width, 24, 12,
                TextAnchor.MiddleLeft, UiPanel.DimTextColor);
        }
    }

    private static void RebuildListBody()
    {
        _listBody.Clear();
        if (_listMode == ListMode.Rules)
            RebuildRulesList();
        else
            RebuildCursorList();
    }

    private static void RebuildCursorList()
    {
        var width = _listBody.Width;
        var stack = PickerController.Stack;
        var selected = PickerController.Selected;
        const float rowHeight = 22f;
        const float buttonsHeight = 64f;
        var maxRows = Mathf.Max(1, (int)((_listBody.Rect.rect.height - buttonsHeight) / (rowHeight + 2f)));

        var y = 0f;
        if (stack.Count == 0)
        {
            _listBody.Label($"Press {UiEditorHotkeys.Describe(PickerController.Hotkeys.Pick)} over a UI element.", 0, y, width, 40, 12,
                TextAnchor.UpperLeft, UiPanel.DimTextColor);
        }

        // Keep the selected row in view.
        var first = Mathf.Clamp(PickerController.StackIndex - maxRows / 2, 0, Mathf.Max(0, stack.Count - maxRows));
        for (var i = first; i < stack.Count && i < first + maxRows; i++)
        {
            var element = stack[i];
            var index = i;
            var isSelected = selected != null && element.RectTransform == selected.RectTransform;
            _listBody.Button(string.Empty, 0, y, width, rowHeight, () => PickerController.SelectStackIndex(index),
                isSelected ? UiPanel.SelectedColor : UiPanel.MutedButtonColor);
            _listBody.Label($"{i + 1}. {PickedElement.Truncate(element.Name, 22)} {Tags(element)}", 6, y, width - 8, rowHeight, 12);
            y += rowHeight + 2f;
        }

        if (stack.Count > first + maxRows)
            _listBody.Label($"+{stack.Count - first - maxRows} more ({CycleText()})", 0, y, width, 18, 11,
                TextAnchor.MiddleLeft, UiPanel.DimTextColor);

        var buttonsY = _listBody.Rect.rect.height - buttonsHeight + 6f;
        var half = (width - 4f) / 2f;
        _listBody.Button("Parent", 0, buttonsY, half, 24, PickerController.SelectParent, UiPanel.MutedButtonColor);
        _listBody.Button("Child", half + 4f, buttonsY, half, 24, PickerController.SelectChild, UiPanel.MutedButtonColor);
        _listBody.Button("Clear selection", 0, buttonsY + 30f, width, 24, PickerController.Clear, UiPanel.MutedButtonColor);
    }

    /// <summary>A rule in the combined Rules list, with the tab that edits it.</summary>
    private sealed class ListedRule
    {
        public int TabIndex;
        public RuleSummary Rule;
    }

    private static readonly Color[] BadgeColors =
    {
        new Color(0.25f, 0.5f, 0.9f, 1f),   // Layout
        new Color(0.25f, 0.65f, 0.35f, 1f), // Text
        new Color(0.85f, 0.55f, 0.2f, 1f),  // Sprite
    };

    private static void RebuildRulesList()
    {
        _rulesListKey = RulesListKey();

        var width = _listBody.Width;
        var rules = CollectRules();
        const float rowHeight = 34f;
        const float footerHeight = 30f;
        const float badgeWidth = 20f;
        var perPage = Mathf.Max(1, (int)((_listBody.Rect.rect.height - footerHeight) / (rowHeight + 2f)));
        var pages = Mathf.Max(1, (rules.Count + perPage - 1) / perPage);
        _rulePage = Mathf.Clamp(_rulePage, 0, pages - 1);

        if (rules.Count == 0)
        {
            var message = string.IsNullOrEmpty(_ruleFilter) ? "No rules yet." : "No rules match the search.";
            _listBody.Label(message, 0, 0, width, 40, 12, TextAnchor.UpperLeft, UiPanel.DimTextColor);
        }

        var openTab = _builtTab;
        var y = 0f;
        for (var i = _rulePage * perPage; i < rules.Count && i < (_rulePage + 1) * perPage; i++)
        {
            var listed = rules[i];
            var rule = listed.Rule;
            var tab = _tabs[listed.TabIndex];
            var isOpen = tab == openTab && rule.Key == tab.EditingRulePath;

            _listBody.Button(string.Empty, 0, y, width, rowHeight, () => OpenRule(listed.TabIndex, rule.Key, rule.Path),
                isOpen ? UiPanel.SelectedColor : UiPanel.MutedButtonColor);
            _listBody.Box(4, y + 7, badgeWidth, badgeWidth, BadgeColors[listed.TabIndex % BadgeColors.Length]);
            _listBody.Label(tab.Title.Substring(0, 1), 4, y + 7, badgeWidth, badgeWidth, 12, TextAnchor.MiddleCenter,
                Color.white, FontStyle.Bold);

            var textX = badgeWidth + 10f;
            _listBody.Label(TruncateStart(rule.Path, 32), textX, y + 1, width - textX - 4, 17, 12);
            var detail = string.IsNullOrEmpty(rule.Description) ? rule.File : $"{rule.Description}  ·  {rule.File}";
            _listBody.Label(PickedElement.Truncate(detail, 38), textX, y + 16, width - textX - 4, 16, 11,
                TextAnchor.MiddleLeft, UiPanel.DimTextColor);
            y += rowHeight + 2f;
        }

        var footerY = _listBody.Rect.rect.height - footerHeight + 4f;
        _listBody.Button("<", 0, footerY, 36, 24, () => { _rulePage--; RebuildListBody(); }, UiPanel.MutedButtonColor);
        _listBody.Label($"{_rulePage + 1}/{pages}  ({rules.Count} rules)", 40, footerY, width - 80, 24, 12,
            TextAnchor.MiddleCenter, UiPanel.DimTextColor);
        _listBody.Button(">", width - 36, footerY, 36, 24, () => { _rulePage++; RebuildListBody(); }, UiPanel.MutedButtonColor);
    }

    /// <summary>
    /// Every tab's rules in one list, filtered by the search box. Sorted by path so an element's
    /// layout rule and resizer sit next to each other.
    /// </summary>
    private static List<ListedRule> CollectRules()
    {
        var result = new List<ListedRule>();
        for (var i = 0; i < _tabs.Count; i++)
        {
            foreach (var rule in FilterRules(_tabs[i].ListRules()))
                result.Add(new ListedRule { TabIndex = i, Rule = rule });
        }

        result.Sort((a, b) =>
        {
            var byPath = string.Compare(a.Rule.Path, b.Rule.Path, StringComparison.OrdinalIgnoreCase);
            return byPath != 0 ? byPath : a.TabIndex.CompareTo(b.TabIndex);
        });
        return result;
    }

    private static bool RulesListIsStale() => RulesListKey() != _rulesListKey;

    // Changes whenever any tab's rules change, or the rule open for editing changes.
    private static string RulesListKey()
    {
        var key = new System.Text.StringBuilder();
        foreach (var tab in _tabs)
            key.Append(tab.RulesVersion).Append('|');
        key.Append(_tabs.IndexOf(_builtTab)).Append('|').Append(_builtTab?.EditingRulePath);
        return key.ToString();
    }

    private static List<RuleSummary> FilterRules(IReadOnlyList<RuleSummary> rules)
    {
        var result = new List<RuleSummary>();
        foreach (var rule in rules)
        {
            if (string.IsNullOrEmpty(_ruleFilter)
                || Contains(rule.Path, _ruleFilter)
                || Contains(rule.Description, _ruleFilter))
            {
                result.Add(rule);
            }
        }
        return result;
    }

    private static bool Contains(string value, string search) =>
        value != null && value.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0;

    private static string TruncateStart(string value, int maxLength)
    {
        if (value == null || value.Length <= maxLength)
            return value;
        return "…" + value.Substring(value.Length - maxLength + 1);
    }

    /// <summary>
    /// Opens a saved rule from the Rules list. If an element it applies to is on screen, that
    /// element is selected (and outlined); otherwise the rule is edited on its own.
    /// </summary>
    private static void OpenRule(int tabIndex, string ruleKey, string rulePath)
    {
        LeaveTab();
        _tabIndex = tabIndex;
        _openRulePath = ruleKey;

        var element = FindElementForRule(rulePath);
        if (element != null)
            PickerController.SelectElement(element);
        else
            PickerController.Clear();

        _selectionDirty = true;
    }

    private static RectTransform FindElementForRule(string rulePath)
    {
        RectTransform wildcardMatch = null;
        foreach (var rect in UiCompat.FindObjectsOfType<RectTransform>())
        {
            if (rect == null || !rect.gameObject.activeInHierarchy)
                continue;

            var path = ObjectHelper.GetGameObjectPath(rect.gameObject);
            if (path.StartsWith(ElementPicker.EditorObjectPrefix))
                continue;
            if (path == rulePath)
                return rect;
            if (wildcardMatch == null && PathPattern.IsWildcard(rulePath) && PathPattern.IsMatch(rulePath, path))
                wildcardMatch = rect;
        }
        return wildcardMatch;
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
        if ((element.Capabilities & ElementCapabilities.SizeFitter) != 0) tags += "F";
        if ((element.Capabilities & ElementCapabilities.LayoutGroup) != 0) tags += "G";
        if (element.IsHidden) tags += "H";
        return tags.Length > 0 ? $"[{tags}]" : string.Empty;
    }

    // ---- Right side --------------------------------------------------------------------------

    private static void RebuildElement()
    {
        var element = PickerController.Selected;
        var rulePath = _openRulePath;
        _openRulePath = null;

        // Re-selecting the element that's already open (e.g. re-picking the same spot) keeps
        // the tab as-is, so in-progress edits aren't thrown away.
        if (rulePath == null && element != null && _builtTab != null && element.RectTransform == _builtElement)
        {
            RebuildHeader(element, _builtRulePath);
            return;
        }

        LeaveTab();
        RebuildHeader(element, rulePath);
        _content.Clear();

        if (element == null && rulePath == null)
        {
            var hint = _listMode == ListMode.Rules
                ? "Choose a rule on the left, or pick an element on screen."
                : $"Press {UiEditorHotkeys.Describe(PickerController.Hotkeys.Pick)} over a UI element to pick it, " +
                  $"then use {CycleText()} or the list to choose. Use Rules to browse saved rules.";
            _content.Label(hint, 0, 0, _content.Width, 60, 13, TextAnchor.UpperLeft, UiPanel.DimTextColor);
            return;
        }

        var tab = _tabs[_tabIndex];
        tab.Build(_content, element, rulePath);
        _builtTab = tab;
        _builtElement = element?.RectTransform;
        _builtRulePath = rulePath;
    }

    private static void RebuildHeader(PickedElement element, string rulePath)
    {
        _header.Clear();
        var width = _header.Width;
        var detached = element == null && rulePath != null;

        if (element != null)
        {
            _header.Label($"{element.Name}  {element.CapabilityTags}", 0, 0, width, 20, 14, TextAnchor.MiddleLeft, null, FontStyle.Bold);
            _header.Label(element.Path, 0, 20, width, 18, 11, TextAnchor.MiddleLeft, UiPanel.DimTextColor);
            _header.Label($"Components: {element.ComponentList}", 0, 38, width, 16, 10, TextAnchor.MiddleLeft, UiPanel.DimTextColor);
        }
        else if (detached)
        {
            _header.Label("Rule (no matching element on screen)", 0, 0, width, 20, 14, TextAnchor.MiddleLeft, UiPanel.WarningColor, FontStyle.Bold);
            _header.Label(rulePath, 0, 20, width, 18, 11, TextAnchor.MiddleLeft, UiPanel.DimTextColor);
        }

        if (element != null && !_tabs[_tabIndex].IsAvailable(element))
            _tabIndex = FirstAvailableTab(element);

        var x = 0f;
        for (var i = 0; i < _tabs.Count; i++)
        {
            var tab = _tabs[i];
            var index = i;
            // A rule opened on its own belongs to one tab; the others don't apply to it.
            var available = detached ? i == _tabIndex : tab.IsAvailable(element);
            var color = i == _tabIndex ? UiPanel.SelectedColor : available ? UiPanel.MutedButtonColor : new Color(0.18f, 0.18f, 0.2f, 1f);
            _header.Button(tab.Title, x, 62, 100, 26, available ? () => SwitchTab(index) : (Action)null, color);
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
        _rulePage = 0;
        RebuildElement();
        RebuildListBody();
    }

    private static void LeaveTab()
    {
        CloseChoice();
        _builtTab?.Leave();
        _builtTab = null;
        _builtElement = null;
        _builtRulePath = null;
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
        _titleBar.Button("Plugin settings", WindowWidth - 168, 3, 130, TitleHeight - 6, ToggleSettings, UiPanel.MutedButtonColor);
        _titleBar.Button("X", WindowWidth - 34, 3, 28, TitleHeight - 6, Close, UiPanel.DangerColor);
        // Stops short of the buttons so clicking them doesn't also start a drag.
        _dragHandle = UiPanel.Create(_titleBar.Rect, "DragHandle", 0, 0, WindowWidth - 174, TitleHeight).Rect;

        var bodyTop = TitleHeight + Padding;
        var bodyHeight = WindowHeight - bodyTop - StatusHeight - Padding;
        _listTop = UiPanel.Create(_window, "ListTop", Padding, bodyTop, ListWidth, ListTopHeight);
        _listBody = UiPanel.Create(_window, "ListBody", Padding, bodyTop + ListTopHeight + 6f, ListWidth, bodyHeight - ListTopHeight - 6f);
        RebuildListTop();

        var rightX = Padding * 2 + ListWidth;
        var rightWidth = WindowWidth - rightX - Padding;
        _header = UiPanel.Create(_window, "Header", rightX, bodyTop, rightWidth, HeaderHeight);
        _content = UiPanel.Create(_window, "Content", rightX, bodyTop + HeaderHeight + 8f, rightWidth,
            bodyHeight - HeaderHeight - 8f);

        // Created after the body panels so it draws over them; hidden until opened.
        _settings = UiPanel.Create(_window, "Settings", Padding, bodyTop, WindowWidth - 2 * Padding, bodyHeight,
            new Color(0.08f, 0.08f, 0.1f, 1f));
        _settings.Rect.gameObject.SetActive(false);

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
}
