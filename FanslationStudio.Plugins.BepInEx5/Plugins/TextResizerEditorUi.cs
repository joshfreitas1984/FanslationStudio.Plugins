using BepInEx;
using FanslationStudio.Plugins.TextResizer;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace FanslationStudio.Plugins.Plugins;

/// <summary>
/// In-game editor for TextResizer .yaml resizers - Mono (BepInEx5) port of the IL2CPP host's
/// TextResizerEditorUi. Layout/behaviour is kept identical between the two; the only difference
/// is that Mono can use the normal generic AddComponent&lt;T&gt;()/FindObjectOfType&lt;T&gt;()/
/// Resources.GetBuiltinResource&lt;T&gt;() APIs directly, since this host compiles against the
/// real UnityEngine assemblies rather than IL2CPP's unhollowed ones. See the IL2CPP version
/// (FanslationStudio.Plugins.BepInEx6.IL2CPP/Plugins/TextResizerEditorUi.cs) for the interop
/// constraints that shaped this design (e.g. avoiding UnityEvent.AddListener in favour of
/// per-frame polling) - those constraints are IL2CPP-specific and not required here, but the
/// polling-based approach works fine on Mono too and keeping both hosts' logic in lockstep makes
/// this much easier to maintain.
/// </summary>
public static class TextResizerEditorUi
{
    private static TextResizerService _service;

    private static GameObject _root;
    private static RectTransform _panel;
    private static Font _cachedFont;
    private static Text _statusLabel;

    private static List<TextResizerContract> _allResizers = new();
    private static int _pageIndex;
    private const int PageSize = 12;

    private static TextResizerContract _selected;

    private static RectTransform _listContainer;
    private static RectTransform _formContainer;

    private static readonly Dictionary<string, InputField> _formInputs = new();
    private static readonly Dictionary<string, string> _lastSeenInputText = new();
    private static readonly List<(RectTransform rect, Action onClick)> _listClickables = new();
    private static readonly List<(RectTransform rect, Action onClick)> _formClickables = new();
    private static readonly List<(RectTransform rect, Action onClick)> _dropdownOverlayClickables = new();
    private static readonly List<(RectTransform rect, Action onClick)> _panelClickables = new();

    private static RectTransform _titleDragRect;
    private static bool _isDragging;
    private static Vector2 _dragPointerStart;
    private static Vector2 _dragPanelStart;

    private static string _openDropdownKey;
    private static (string key, string[] options, float fieldY)? _pendingDropdownOverlay;

    private static readonly string[] AlignmentOptions =
        new[] { string.Empty }.Concat(Enum.GetNames(typeof(TextAlignmentOptions))).ToArray();
    private static readonly string[] OverflowModeOptions =
        new[] { string.Empty }.Concat(Enum.GetNames(typeof(TextOverflowModes))).ToArray();

    private const string WindowPosPrefKeyX = "FSTextResizerEditor.WindowX";
    private const string WindowPosPrefKeyY = "FSTextResizerEditor.WindowY";

    // The dictionary key the currently-selected resizer was actually loaded/saved under -
    // distinct from _selected.Path once the user starts editing the path field, so Save/Delete
    // know which existing entry to replace/remove rather than acting on the unsaved new path.
    private static string _selectedOriginalPath;

    // Lets the editor notice resizers added out-of-band (hotkeys, Reload) while it's open, and
    // refresh the list without the user having to close/reopen it. See ResizersVersion.
    private static int _lastKnownResizersVersion = -1;

    public static bool IsOpen => _root != null;

    public static void Configure(TextResizerService service)
    {
        _service = service;
    }

    public static void Toggle()
    {
        if (_root != null)
            Close();
        else
            Open();
    }

    public static void Open()
    {
        if (_root != null || _service == null)
            return;

        _allResizers = TextResizerService.GetAllResizers();
        _lastKnownResizersVersion = TextResizerService.ResizersVersion;
        _pageIndex = 0;
        _selected = null;
        _selectedOriginalPath = null;

        BuildRoot();

        var newlyAddedPath = TextResizerService.LastAddedResizerPath;
        TextResizerService.LastAddedResizerPath = null;
        if (newlyAddedPath != null && TextResizerService.Resizers.ContainsKey(newlyAddedPath))
        {
            // SelectResizer rebuilds both the list and form panels itself.
            SelectResizer(newlyAddedPath);
        }
        else
        {
            RebuildListPanel();
            RebuildFormPanel();
        }
    }

    public static void Close()
    {
        if (_root == null)
            return;

        UnityEngine.Object.Destroy(_root);
        _root = null;
        _panel = null;
        _titleDragRect = null;
        _listContainer = null;
        _formContainer = null;
        _statusLabel = null;
        _formInputs.Clear();
        _lastSeenInputText.Clear();
        _listClickables.Clear();
        _formClickables.Clear();
        _dropdownOverlayClickables.Clear();
        _panelClickables.Clear();
        _openDropdownKey = null;
        _isDragging = false;
        _selected = null;
        _selectedOriginalPath = null;
    }

    /// <summary>Must be called every frame (e.g. from the plugin's Update) while the editor may be open.</summary>
    public static void Tick()
    {
        if (_root == null)
            return;

        PollResizersVersion();
        PollInputChanges();
        PollDrag();
        PollClicks();
    }

    private static void PollResizersVersion()
    {
        if (TextResizerService.ResizersVersion == _lastKnownResizersVersion)
            return;

        _lastKnownResizersVersion = TextResizerService.ResizersVersion;
        _allResizers = TextResizerService.GetAllResizers();

        // If the selected resizer got removed out-of-band (e.g. Reload), drop the selection
        // rather than keep editing a contract that's no longer backed by anything.
        if (_selectedOriginalPath != null && !TextResizerService.Resizers.ContainsKey(_selectedOriginalPath))
        {
            _selected = null;
            _selectedOriginalPath = null;
            RebuildFormPanel();
        }

        // If a resizer was just added out-of-band (hotkey), save whatever's currently being
        // edited (SelectResizer does this when switching) and select the new one instead.
        var newlyAddedPath = TextResizerService.LastAddedResizerPath;
        TextResizerService.LastAddedResizerPath = null;
        if (newlyAddedPath != null && TextResizerService.Resizers.ContainsKey(newlyAddedPath))
        {
            SelectResizer(newlyAddedPath);
            return;
        }

        RebuildListPanel();
    }

    private static void PollInputChanges()
    {
        foreach (var kv in _formInputs)
        {
            var key = kv.Key;
            var inputField = kv.Value;
            if (inputField == null)
                continue;

            var currentText = inputField.text;
            if (_lastSeenInputText.TryGetValue(key, out var last) && last == currentText)
                continue;

            _lastSeenInputText[key] = currentText;
            ApplyFieldValue(key, currentText);
        }
    }

    private static void PollDrag()
    {
        if (_titleDragRect == null || _panel == null)
            return;

        if (!_isDragging)
        {
            if (!UnityInput.Current.GetMouseButtonDown(0))
                return;

            var downPos = UnityInput.Current.mousePosition;
            var downScreenPoint = new Vector2(downPos.x, downPos.y);
            if (!RectTransformUtility.RectangleContainsScreenPoint(_titleDragRect, downScreenPoint, null))
                return;

            _isDragging = true;
            _dragPointerStart = downScreenPoint;
            _dragPanelStart = _panel.anchoredPosition;
            return;
        }

        if (!UnityInput.Current.GetMouseButton(0))
        {
            _isDragging = false;
            SaveWindowPosition(_panel.anchoredPosition);
            return;
        }

        var pos = UnityInput.Current.mousePosition;
        var screenPoint = new Vector2(pos.x, pos.y);
        _panel.anchoredPosition = _dragPanelStart + (screenPoint - _dragPointerStart);
    }

    private static void PollClicks()
    {
        if (_isDragging || !UnityInput.Current.GetMouseButtonDown(0))
            return;

        var pos = UnityInput.Current.mousePosition;
        var screenPoint = new Vector2(pos.x, pos.y);

        foreach (var (rect, onClick) in _dropdownOverlayClickables)
        {
            if (rect != null && RectTransformUtility.RectangleContainsScreenPoint(rect, screenPoint, null))
            {
                onClick();
                return;
            }
        }

        foreach (var (rect, onClick) in _panelClickables)
        {
            if (rect != null && RectTransformUtility.RectangleContainsScreenPoint(rect, screenPoint, null))
            {
                onClick();
                return;
            }
        }

        if (_openDropdownKey != null)
        {
            // Clicking anywhere outside the open dropdown's own options closes it without
            // also triggering whatever's underneath (e.g. a button the overlay was covering).
            _openDropdownKey = null;
            RebuildFormPanel();
            return;
        }

        foreach (var (rect, onClick) in _formClickables)
        {
            if (rect != null && RectTransformUtility.RectangleContainsScreenPoint(rect, screenPoint, null))
            {
                onClick();
                return;
            }
        }

        foreach (var (rect, onClick) in _listClickables)
        {
            if (rect != null && RectTransformUtility.RectangleContainsScreenPoint(rect, screenPoint, null))
            {
                onClick();
                return;
            }
        }
    }

    private static void ApplyFieldValue(string key, string text)
    {
        if (_selected == null)
            return;

        float? ParseFloat() => float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : (float?)null;

        if (key == nameof(TextResizerContract.Path))
        {
            // Path is the dictionary key in TextResizerService.Resizers - previewing it live
            // (like the other fields) would insert a second, unsaved entry under the new key
            // while the original key's entry is still present, since _selected/the dictionary
            // value are the same object. Only apply the rename for real when Save is clicked
            // (see SaveSelected/_selectedOriginalPath), so just update the in-memory field here.
            _selected.Path = text ?? string.Empty;
            SetStatus("Path changed - click Save to rename (unsaved).");
            return;
        }

        switch (key)
        {
            case nameof(TextResizerContract.IdealFontSize): _selected.IdealFontSize = ParseFloat(); break;
            case nameof(TextResizerContract.FontPercentage): _selected.FontPercentage = ParseFloat(); break;
            case nameof(TextResizerContract.MinFontSize): _selected.MinFontSize = ParseFloat(); break;
            case nameof(TextResizerContract.MaxFontSize): _selected.MaxFontSize = ParseFloat(); break;
            case nameof(TextResizerContract.LineSpacing): _selected.LineSpacing = ParseFloat(); break;
            case nameof(TextResizerContract.CharacterSpacing): _selected.CharacterSpacing = ParseFloat(); break;
            case nameof(TextResizerContract.WordSpacing): _selected.WordSpacing = ParseFloat(); break;
            case nameof(TextResizerContract.AdjustX): _selected.AdjustX = ParseFloat() ?? 0; break;
            case nameof(TextResizerContract.AdjustY): _selected.AdjustY = ParseFloat() ?? 0; break;
            case nameof(TextResizerContract.AdjustWidth): _selected.AdjustWidth = ParseFloat() ?? 0; break;
            case nameof(TextResizerContract.AdjustHeight): _selected.AdjustHeight = ParseFloat() ?? 0; break;
            case nameof(TextResizerContract.Alignment): _selected.Alignment = text ?? string.Empty; break;
            case nameof(TextResizerContract.OverflowMode): _selected.OverflowMode = text ?? string.Empty; break;
            case nameof(TextResizerContract.SampleText): _selected.SampleText = text ?? string.Empty; break;
        }

        _service.PreviewResizer(_selected);
        SetStatus("Previewing unsaved changes - click Save to persist.");
    }

    private static void BuildRoot()
    {
        _root = new GameObject("FSTextResizerEditor");

        var canvas = _root.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = short.MaxValue;

        _root.AddComponent<CanvasScaler>();
        _root.AddComponent<GraphicRaycaster>();

        EnsureEventSystemExists();

        var panelGo = new GameObject("Panel");
        panelGo.transform.SetParent(_root.transform, false);
        var panelImage = panelGo.AddComponent<Image>();
        panelImage.color = new Color(0.08f, 0.08f, 0.08f, 0.95f);
        _panel = panelGo.transform as RectTransform;
        _panel.anchorMin = new Vector2(0.5f, 0.5f);
        _panel.anchorMax = new Vector2(0.5f, 0.5f);
        _panel.pivot = new Vector2(0.5f, 0.5f);
        _panel.sizeDelta = new Vector2(920, 640);
        _panel.anchoredPosition = LoadWindowPosition();

        var dragHandleGo = new GameObject("TitleDragHandle");
        dragHandleGo.transform.SetParent(_panel, false);
        var dragHandleImage = dragHandleGo.AddComponent<Image>();
        dragHandleImage.color = new Color(0f, 0f, 0f, 0f);
        _titleDragRect = dragHandleGo.transform as RectTransform;
        _titleDragRect.anchorMin = new Vector2(0, 1);
        _titleDragRect.anchorMax = new Vector2(1, 1);
        _titleDragRect.pivot = new Vector2(0.5f, 1);
        _titleDragRect.sizeDelta = new Vector2(0, 30);
        _titleDragRect.anchoredPosition = Vector2.zero;

        CreateLabel(_panel, "Title", "TextResizer Editor  (drag title bar to move)", new Vector2(10, -10), new Vector2(800, 24), 16, TextAnchor.UpperLeft, Color.white);
        _statusLabel = CreateLabel(_panel, "Status", string.Empty, new Vector2(10, -614), new Vector2(880, 20), 12, TextAnchor.UpperLeft, new Color(1f, 0.85f, 0.3f));
    }

    private static Vector2 LoadWindowPosition()
    {
        var x = PlayerPrefs.GetFloat(WindowPosPrefKeyX, 0f);
        var y = PlayerPrefs.GetFloat(WindowPosPrefKeyY, 0f);
        return new Vector2(x, y);
    }

    private static void SaveWindowPosition(Vector2 position)
    {
        PlayerPrefs.SetFloat(WindowPosPrefKeyX, position.x);
        PlayerPrefs.SetFloat(WindowPosPrefKeyY, position.y);
        PlayerPrefs.Save();
    }

    private static void EnsureEventSystemExists()
    {
        var existing = UnityEngine.Object.FindObjectOfType<EventSystem>();
        if (existing != null)
            return;

        var eventSystemGo = new GameObject("FSTextResizerEventSystem");
        eventSystemGo.AddComponent<EventSystem>();
        eventSystemGo.AddComponent<StandaloneInputModule>();
    }

    private static void RebuildListPanel()
    {
        if (_listContainer != null)
            UnityEngine.Object.Destroy(_listContainer.gameObject);
        _listClickables.Clear();

        var containerGo = new GameObject("ListContainer");
        containerGo.AddComponent<RectTransform>();
        containerGo.transform.SetParent(_panel, false);
        _listContainer = containerGo.transform as RectTransform;
        _listContainer.anchorMin = new Vector2(0, 1);
        _listContainer.anchorMax = new Vector2(0, 1);
        _listContainer.pivot = new Vector2(0, 1);
        _listContainer.sizeDelta = new Vector2(280, 560);
        _listContainer.anchoredPosition = new Vector2(10, -40);

        var pageItems = _allResizers.Skip(_pageIndex * PageSize).Take(PageSize).ToList();
        var totalPages = Math.Max(1, (int)Math.Ceiling(_allResizers.Count / (double)PageSize));

        CreateLabel(_listContainer, "PageInfo", $"Resizers ({_allResizers.Count}) - page {_pageIndex + 1}/{totalPages}",
            new Vector2(0, 0), new Vector2(280, 20), 12, TextAnchor.UpperLeft, Color.white);

        for (var i = 0; i < pageItems.Count; i++)
        {
            var contract = pageItems[i];
            var y = -24 - i * 26;

            var itemGo = new GameObject($"Item_{i}");
            itemGo.transform.SetParent(_listContainer, false);
            var isSelected = _selected != null && _selected.Path == contract.Path;
            var image = itemGo.AddComponent<Image>();
            image.color = isSelected ? new Color(0.3f, 0.5f, 0.8f) : new Color(0.2f, 0.2f, 0.2f, 0.9f);
            var itemRect = itemGo.transform as RectTransform;
            itemRect.anchorMin = new Vector2(0, 1);
            itemRect.anchorMax = new Vector2(0, 1);
            itemRect.pivot = new Vector2(0, 1);
            itemRect.sizeDelta = new Vector2(280, 24);
            itemRect.anchoredPosition = new Vector2(0, y);

            var labelGo = new GameObject("Label");
            labelGo.transform.SetParent(itemGo.transform, false);
            var label = labelGo.AddComponent<Text>();
            label.font = GetBuiltinFont();
            label.fontSize = 11;
            label.color = Color.white;
            label.alignment = TextAnchor.MiddleLeft;
            label.text = TruncatePath(contract.Path, 46);
            label.horizontalOverflow = HorizontalWrapMode.Overflow;
            var labelRect = labelGo.transform as RectTransform;
            labelRect.anchorMin = Vector2.zero;
            labelRect.anchorMax = Vector2.one;
            labelRect.offsetMin = new Vector2(4, 0);
            labelRect.offsetMax = new Vector2(-4, 0);

            var path = contract.Path;
            _listClickables.Add((itemRect, () => SelectResizer(path)));
        }

        var navY = -24 - PageSize * 26 - 10;
        CreateButton(_listClickables, _listContainer, "< Prev", new Vector2(0, navY), new Vector2(80, 24), () =>
        {
            if (_pageIndex > 0) { _pageIndex--; RebuildListPanel(); }
        });
        CreateButton(_listClickables, _listContainer, "Next >", new Vector2(90, navY), new Vector2(80, 24), () =>
        {
            if ((_pageIndex + 1) * PageSize < _allResizers.Count) { _pageIndex++; RebuildListPanel(); }
        });
        CreateButton(_listClickables, _listContainer, "Refresh", new Vector2(180, navY), new Vector2(90, 24), () =>
        {
            _allResizers = TextResizerService.GetAllResizers();
            _pageIndex = 0;
            RebuildListPanel();
        });
    }

    private static void SelectResizer(string path)
    {
        if (_selected != null && _selectedOriginalPath != path)
            SaveSelected();

        if (!TextResizerService.Resizers.TryGetValue(path, out var contract))
            return;

        _selected = contract.ShallowClone();
        _selectedOriginalPath = path;
        _openDropdownKey = null;
        RebuildListPanel();
        RebuildFormPanel();
        SetStatus($"Editing '{path}'");
    }

    private static void RebuildFormPanel()
    {
        if (_formContainer != null)
            UnityEngine.Object.Destroy(_formContainer.gameObject);
        _formClickables.Clear();
        _dropdownOverlayClickables.Clear();
        _formInputs.Clear();
        _lastSeenInputText.Clear();

        var containerGo = new GameObject("FormContainer");
        containerGo.AddComponent<RectTransform>();
        containerGo.transform.SetParent(_panel, false);
        _formContainer = containerGo.transform as RectTransform;
        _formContainer.anchorMin = new Vector2(0, 1);
        _formContainer.anchorMax = new Vector2(0, 1);
        _formContainer.pivot = new Vector2(0, 1);
        _formContainer.sizeDelta = new Vector2(600, 560);
        _formContainer.anchoredPosition = new Vector2(300, -40);

        if (_selected == null)
        {
            CreateLabel(_formContainer, "Hint", "Select a resizer from the list to edit it.", new Vector2(0, 0), new Vector2(590, 24), 13, TextAnchor.UpperLeft, Color.white);
            return;
        }

        const float rowHeight = 26f;
        var y = 0f;

        y = CreateStringFieldRow(nameof(TextResizerContract.Path), "Path (wildcards allowed)", _selected.Path, y, rowHeight, inputWidth: 390);
        y = CreateStringFieldRow(nameof(TextResizerContract.SampleText), "Sample Text", _selected.SampleText, y, rowHeight, inputWidth: 390);
        y -= 6;

        y = CreateFloatFieldRow(nameof(TextResizerContract.IdealFontSize), "Ideal Font Size", _selected.IdealFontSize, y, rowHeight);
        y = CreateFloatFieldRow(nameof(TextResizerContract.FontPercentage), "Font Percentage", _selected.FontPercentage, y, rowHeight);
        y = CreateFloatFieldRow(nameof(TextResizerContract.MinFontSize), "Min Font Size", _selected.MinFontSize, y, rowHeight);
        y = CreateFloatFieldRow(nameof(TextResizerContract.MaxFontSize), "Max Font Size", _selected.MaxFontSize, y, rowHeight);
        y = CreateEnumDropdownRow(nameof(TextResizerContract.Alignment), "Alignment", _selected.Alignment, AlignmentOptions, y, rowHeight);
        y = CreateEnumDropdownRow(nameof(TextResizerContract.OverflowMode), "Overflow Mode", _selected.OverflowMode, OverflowModeOptions, y, rowHeight);
        y = CreateFloatFieldRow(nameof(TextResizerContract.LineSpacing), "Line Spacing", _selected.LineSpacing, y, rowHeight);
        y = CreateFloatFieldRow(nameof(TextResizerContract.CharacterSpacing), "Character Spacing", _selected.CharacterSpacing, y, rowHeight);
        y = CreateFloatFieldRow(nameof(TextResizerContract.WordSpacing), "Word Spacing", _selected.WordSpacing, y, rowHeight);
        y = CreateFloatFieldRow(nameof(TextResizerContract.AdjustX), "Adjust X", _selected.AdjustX, y, rowHeight);
        y = CreateFloatFieldRow(nameof(TextResizerContract.AdjustY), "Adjust Y", _selected.AdjustY, y, rowHeight);
        y = CreateFloatFieldRow(nameof(TextResizerContract.AdjustWidth), "Adjust Width", _selected.AdjustWidth, y, rowHeight);
        y = CreateFloatFieldRow(nameof(TextResizerContract.AdjustHeight), "Adjust Height", _selected.AdjustHeight, y, rowHeight);

        y -= 6;
        y = CreateTriStateToggleRow("Allow Word Wrap", () => _selected.AllowWordWrap, v => _selected.AllowWordWrap = v, y, rowHeight);
        y = CreateTriStateToggleRow("Allow Auto Sizing", () => _selected.AllowAutoSizing, v => _selected.AllowAutoSizing = v, y, rowHeight);
        y = CreateBinaryToggleRow("Allow Left Trim Text", () => _selected.AllowLeftTrimText, v => _selected.AllowLeftTrimText = v, y, rowHeight);

        y -= 10;
        CreateButton(_formClickables, _formContainer, "Save", new Vector2(0, y), new Vector2(90, 28), SaveSelected, new Color(0.2f, 0.6f, 0.2f));
        CreateButton(_formClickables, _formContainer, "Delete", new Vector2(100, y), new Vector2(90, 28), DeleteSelected, new Color(0.6f, 0.2f, 0.2f));
        CreateButton(_formClickables, _formContainer, "Close", new Vector2(200, y), new Vector2(90, 28), Close, new Color(0.35f, 0.35f, 0.35f));

        if (_pendingDropdownOverlay.HasValue)
        {
            var (overlayKey, overlayOptions, overlayFieldY) = _pendingDropdownOverlay.Value;
            BuildDropdownOverlay(overlayKey, overlayOptions, overlayFieldY);
            _pendingDropdownOverlay = null;
        }
    }

    private static void SaveSelected()
    {
        if (_selected == null)
            return;

        _service.SaveResizer(_selected, _selectedOriginalPath);
        _selectedOriginalPath = _selected.Path;
        _lastKnownResizersVersion = TextResizerService.ResizersVersion;
        _allResizers = TextResizerService.GetAllResizers();
        SetStatus($"Saved '{_selected.Path}'.");
        RebuildListPanel();
    }

    private static void DeleteSelected()
    {
        if (_selected == null)
            return;

        var path = _selectedOriginalPath ?? _selected.Path;
        _service.DeleteResizer(path);
        _selected = null;
        _selectedOriginalPath = null;
        _lastKnownResizersVersion = TextResizerService.ResizersVersion;
        _allResizers = TextResizerService.GetAllResizers();
        SetStatus($"Deleted '{path}'.");
        RebuildListPanel();
        RebuildFormPanel();
    }

    private static float CreateFloatFieldRow(string key, string label, float? value, float y, float rowHeight)
    {
        CreateLabel(_formContainer, $"Label_{key}", label, new Vector2(0, y), new Vector2(180, rowHeight - 2), 12, TextAnchor.MiddleLeft, Color.white);
        var input = CreateInputField(_formContainer, $"Input_{key}", new Vector2(190, y), new Vector2(120, rowHeight - 2),
            value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) : string.Empty);
        _formInputs[key] = input;
        _lastSeenInputText[key] = input.text;
        return y - rowHeight;
    }

    private static float CreateStringFieldRow(string key, string label, string value, float y, float rowHeight, float inputWidth = 200f)
    {
        CreateLabel(_formContainer, $"Label_{key}", label, new Vector2(0, y), new Vector2(180, rowHeight - 2), 12, TextAnchor.MiddleLeft, Color.white);
        var input = CreateInputField(_formContainer, $"Input_{key}", new Vector2(190, y), new Vector2(inputWidth, rowHeight - 2), value ?? string.Empty);
        _formInputs[key] = input;
        _lastSeenInputText[key] = input.text;
        return y - rowHeight;
    }

    // A dropdown-style field for an enum-backed string value that also allows a blank/"null"
    // selection. Deliberately not built on Unity's Dropdown component (which stores its options
    // in a generic List<OptionData> and relies on UnityEvent.onValueChanged) - built from the
    // same plain Image/Text/click-polling primitives already proven safe elsewhere in this file.
    // Clicking the field toggles a popup list of options (rendered by BuildDropdownOverlay,
    // deferred to the end of RebuildFormPanel so it draws on top of everything else).
    private static float CreateEnumDropdownRow(string key, string label, string value, string[] options, float y, float rowHeight)
    {
        CreateLabel(_formContainer, $"Label_{key}", label, new Vector2(0, y), new Vector2(180, rowHeight - 2), 12, TextAnchor.MiddleLeft, Color.white);

        var displayText = string.IsNullOrEmpty(value) ? "(none)" : value;

        var go = new GameObject($"Dropdown_{key}");
        go.transform.SetParent(_formContainer, false);
        var image = go.AddComponent<Image>();
        image.color = new Color(0.25f, 0.25f, 0.25f, 1f);
        var rect = go.transform as RectTransform;
        rect.anchorMin = new Vector2(0, 1);
        rect.anchorMax = new Vector2(0, 1);
        rect.pivot = new Vector2(0, 1);
        rect.sizeDelta = new Vector2(200, rowHeight - 2);
        rect.anchoredPosition = new Vector2(190, y);

        var labelGo = new GameObject("Label");
        labelGo.transform.SetParent(go.transform, false);
        var textLabel = labelGo.AddComponent<Text>();
        textLabel.font = GetBuiltinFont();
        textLabel.fontSize = 12;
        textLabel.color = Color.white;
        textLabel.alignment = TextAnchor.MiddleLeft;
        textLabel.text = $"{displayText}  \u25BC";
        var labelRect = labelGo.transform as RectTransform;
        labelRect.anchorMin = Vector2.zero;
        labelRect.anchorMax = Vector2.one;
        labelRect.offsetMin = new Vector2(6, 0);
        labelRect.offsetMax = new Vector2(-6, 0);

        _formClickables.Add((rect, () =>
        {
            _openDropdownKey = _openDropdownKey == key ? null : key;
            RebuildFormPanel();
        }
        ));

        if (_openDropdownKey == key)
            _pendingDropdownOverlay = (key, options, y);

        return y - rowHeight;
    }

    private static void BuildDropdownOverlay(string key, string[] options, float fieldY)
    {
        const float optionRowHeight = 22f;

        var overlayGo = new GameObject("DropdownOverlay");
        overlayGo.transform.SetParent(_formContainer, false);
        var overlayImage = overlayGo.AddComponent<Image>();
        overlayImage.color = new Color(0.12f, 0.12f, 0.12f, 0.98f);
        var overlayRect = overlayGo.transform as RectTransform;
        overlayRect.anchorMin = new Vector2(0, 1);
        overlayRect.anchorMax = new Vector2(0, 1);
        overlayRect.pivot = new Vector2(0, 1);
        overlayRect.sizeDelta = new Vector2(200, options.Length * optionRowHeight + 4);
        overlayRect.anchoredPosition = new Vector2(190, fieldY - 24);

        for (var i = 0; i < options.Length; i++)
        {
            var optionValue = options[i];

            var itemGo = new GameObject($"Option_{i}");
            itemGo.transform.SetParent(overlayRect, false);
            var itemImage = itemGo.AddComponent<Image>();
            itemImage.color = new Color(0.2f, 0.2f, 0.2f, 1f);
            var itemRect = itemGo.transform as RectTransform;
            itemRect.anchorMin = new Vector2(0, 1);
            itemRect.anchorMax = new Vector2(0, 1);
            itemRect.pivot = new Vector2(0, 1);
            itemRect.sizeDelta = new Vector2(196, optionRowHeight - 2);
            itemRect.anchoredPosition = new Vector2(2, -2 - i * optionRowHeight);

            var itemLabelGo = new GameObject("Label");
            itemLabelGo.transform.SetParent(itemGo.transform, false);
            var itemLabel = itemLabelGo.AddComponent<Text>();
            itemLabel.font = GetBuiltinFont();
            itemLabel.fontSize = 12;
            itemLabel.color = Color.white;
            itemLabel.alignment = TextAnchor.MiddleLeft;
            itemLabel.text = string.IsNullOrEmpty(optionValue) ? "(none)" : optionValue;
            var itemLabelRect = itemLabelGo.transform as RectTransform;
            itemLabelRect.anchorMin = Vector2.zero;
            itemLabelRect.anchorMax = Vector2.one;
            itemLabelRect.offsetMin = new Vector2(6, 0);
            itemLabelRect.offsetMax = new Vector2(-6, 0);

            _dropdownOverlayClickables.Add((itemRect, () =>
            {
                ApplyFieldValue(key, optionValue);
                _openDropdownKey = null;
                RebuildFormPanel();
            }
            ));
        }
    }

    private static float CreateTriStateToggleRow(string label, Func<bool?> getValue, Action<bool?> setValue, float y, float rowHeight)
    {
        CreateLabel(_formContainer, $"Label_{label}", $"{label} (click: unset/on/off)", new Vector2(0, y), new Vector2(300, rowHeight - 2), 12, TextAnchor.MiddleLeft, Color.white);

        var go = new GameObject($"Toggle_{label}");
        go.transform.SetParent(_formContainer, false);
        var image = go.AddComponent<Image>();
        var rect = go.transform as RectTransform;
        rect.anchorMin = new Vector2(0, 1);
        rect.anchorMax = new Vector2(0, 1);
        rect.pivot = new Vector2(0, 1);
        rect.sizeDelta = new Vector2(24, rowHeight - 4);
        rect.anchoredPosition = new Vector2(310, y);

        void Refresh()
        {
            var v = getValue();
            image.color = v == true ? new Color(0.2f, 0.7f, 0.2f) : v == false ? new Color(0.7f, 0.2f, 0.2f) : new Color(0.5f, 0.5f, 0.5f);
        }
        Refresh();

        _formClickables.Add((rect, () =>
        {
            var current = getValue();
            bool? next = current == null ? true : current == true ? (bool?)false : null;
            setValue(next);
            Refresh();
            _service.PreviewResizer(_selected);
            SetStatus("Previewing unsaved changes - click Save to persist.");
        }
        ));

        return y - rowHeight;
    }

    private static float CreateBinaryToggleRow(string label, Func<bool> getValue, Action<bool> setValue, float y, float rowHeight)
    {
        CreateLabel(_formContainer, $"Label_{label}", $"{label} (click to toggle)", new Vector2(0, y), new Vector2(300, rowHeight - 2), 12, TextAnchor.MiddleLeft, Color.white);

        var go = new GameObject($"Toggle_{label}");
        go.transform.SetParent(_formContainer, false);
        var image = go.AddComponent<Image>();
        var rect = go.transform as RectTransform;
        rect.anchorMin = new Vector2(0, 1);
        rect.anchorMax = new Vector2(0, 1);
        rect.pivot = new Vector2(0, 1);
        rect.sizeDelta = new Vector2(24, rowHeight - 4);
        rect.anchoredPosition = new Vector2(310, y);

        void Refresh()
        {
            image.color = getValue() ? new Color(0.2f, 0.7f, 0.2f) : new Color(0.5f, 0.5f, 0.5f);
        }
        Refresh();

        _formClickables.Add((rect, () =>
        {
            setValue(!getValue());
            Refresh();
            _service.PreviewResizer(_selected);
            SetStatus("Previewing unsaved changes - click Save to persist.");
        }
        ));

        return y - rowHeight;
    }

    private static void SetStatus(string text)
    {
        if (_statusLabel != null)
            _statusLabel.text = text;
    }

    private static Text CreateLabel(RectTransform parent, string name, string text, Vector2 topLeftOffset, Vector2 size, int fontSize, TextAnchor alignment, Color color)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        var label = go.AddComponent<Text>();
        label.font = GetBuiltinFont();
        label.fontSize = fontSize;
        label.text = text;
        label.color = color;
        label.alignment = alignment;
        label.horizontalOverflow = HorizontalWrapMode.Overflow;
        label.verticalOverflow = VerticalWrapMode.Truncate;

        var rect = go.transform as RectTransform;
        rect.anchorMin = new Vector2(0, 1);
        rect.anchorMax = new Vector2(0, 1);
        rect.pivot = new Vector2(0, 1);
        rect.sizeDelta = size;
        rect.anchoredPosition = topLeftOffset;
        return label;
    }

    private static InputField CreateInputField(RectTransform parent, string name, Vector2 topLeftOffset, Vector2 size, string initialText)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        var image = go.AddComponent<Image>();
        image.color = new Color(1f, 1f, 1f, 0.9f);
        var rect = go.transform as RectTransform;
        rect.anchorMin = new Vector2(0, 1);
        rect.anchorMax = new Vector2(0, 1);
        rect.pivot = new Vector2(0, 1);
        rect.sizeDelta = size;
        rect.anchoredPosition = topLeftOffset;

        var textGo = new GameObject("Text");
        textGo.transform.SetParent(go.transform, false);
        var text = textGo.AddComponent<Text>();
        text.font = GetBuiltinFont();
        text.fontSize = 13;
        text.color = Color.black;
        text.alignment = TextAnchor.MiddleLeft;
        var textRect = textGo.transform as RectTransform;
        textRect.anchorMin = Vector2.zero;
        textRect.anchorMax = Vector2.one;
        textRect.offsetMin = new Vector2(6, 2);
        textRect.offsetMax = new Vector2(-6, -2);

        var inputField = go.AddComponent<InputField>();
        inputField.textComponent = text;
        inputField.text = initialText ?? string.Empty;

        return inputField;
    }

    private static void CreateButton(List<(RectTransform rect, Action onClick)> targetList, RectTransform parent, string text, Vector2 topLeftOffset, Vector2 size, Action onClick, Color? color = null)
    {
        var go = new GameObject($"Button_{text}");
        go.transform.SetParent(parent, false);
        var image = go.AddComponent<Image>();
        image.color = color ?? new Color(0.25f, 0.45f, 0.85f, 1f);
        var rect = go.transform as RectTransform;
        rect.anchorMin = new Vector2(0, 1);
        rect.anchorMax = new Vector2(0, 1);
        rect.pivot = new Vector2(0, 1);
        rect.sizeDelta = size;
        rect.anchoredPosition = topLeftOffset;

        var labelGo = new GameObject("Label");
        labelGo.transform.SetParent(go.transform, false);
        var label = labelGo.AddComponent<Text>();
        label.font = GetBuiltinFont();
        label.fontSize = 13;
        label.color = Color.white;
        label.alignment = TextAnchor.MiddleCenter;
        label.text = text;
        var labelRect = labelGo.transform as RectTransform;
        labelRect.anchorMin = Vector2.zero;
        labelRect.anchorMax = Vector2.one;
        labelRect.offsetMin = Vector2.zero;
        labelRect.offsetMax = Vector2.zero;

        targetList.Add((rect, onClick));
    }

    private static string TruncatePath(string path, int max)
    {
        if (string.IsNullOrEmpty(path) || path.Length <= max)
            return path;
        return "..." + path.Substring(path.Length - max);
    }

    private static Font GetBuiltinFont()
    {
        if (_cachedFont != null)
            return _cachedFont;

        _cachedFont = Resources.GetBuiltinResource<Font>("Arial.ttf");
        return _cachedFont;
    }
}
