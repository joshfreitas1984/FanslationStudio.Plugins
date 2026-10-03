using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using FanslationStudio.Plugins.TextResizer;
using FanslationStudio.Plugins.UnityShared.Editor.Ui;
using FanslationStudio.Plugins.UnityShared.Layout;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace FanslationStudio.Plugins.UnityShared.Editor.Tabs;

/// <summary>
/// Edits the text resizer for the selected element (BepInEx/resizers/*.yaml), through the
/// running <see cref="TextResizerService"/>. Same flow as the Layout tab: throttled live
/// preview, Save/Discard/Delete, auto-save when leaving.
/// </summary>
internal sealed class TextTab : IEditorTab
{
    private const float RowHeight = 24f;
    private const float RowStep = 28f;
    private const float LabelWidth = 104f;
    private const float FieldWidth = 64f;
    private const float Gap = 4f;
    private const float PreviewInterval = 0.15f;
    private static readonly float[] NudgeSteps = { 1f, 5f, 10f, 50f };

    private UiPanel _panel;
    private PickedElement _element;
    private TextResizerContract _working;
    private string _savedPath;
    private string _previewPath;
    // A wildcard resizer this element falls back to when it has no rule of its own - shown as a
    // note in RenderInfo, never loaded into _working (see Build()).
    private TextResizerContract _matchingWildcard;
    private bool _dirty;
    private bool _hasPreview;
    private float _lastPreviewTime;
    private int _nudgeIndex;
    // Which text types' options to offer: the element's own, or both for a rule not on screen.
    private bool _showTmp;
    private bool _showLegacy;
    private CurrentValues _current;

    private InputField _adjustX, _adjustY, _adjustWidth, _adjustHeight;
    // Both adjust rows' "step" buttons share _nudgeIndex, so both relabel when either is clicked.
    private readonly List<UiButton> _stepButtons = new List<UiButton>();

    // Option lists per (TMP, legacy) combination: index = (showTmp ? 2 : 0) + (showLegacy ? 1 : 0).
    private static readonly IList<string>[] AlignmentOptionsCache = new IList<string>[4];
    private static readonly IList<string>[] OverflowOptionsCache = new IList<string>[4];

    private struct CurrentValues
    {
        public float FontSize, MinSize, MaxSize, LineSpacing, CharacterSpacing, WordSpacing;
    }

    private static TextResizerService Service => TextResizerService.Instance;

    public string Title => "Text";

    public bool IsAvailable(PickedElement element) =>
        Service != null && (element == null || (element.Capabilities & ElementCapabilities.Text) != 0);

    public string EditingRulePath => _savedPath;

    public int RulesVersion => TextResizerService.ResizersVersion;

    public IReadOnlyList<RuleSummary> ListRules()
    {
        var rules = new List<RuleSummary>();
        if (Service == null)
            return rules;

        foreach (var resizer in TextResizerService.GetAllResizers())
        {
            rules.Add(new RuleSummary
            {
                Key = resizer.Path,
                Path = resizer.Path,
                Description = resizer.SampleText,
                File = TextResizerService.ResizerSourceFiles.TryGetValue(resizer.Path, out var file) ? Path.GetFileName(file) : "(unsaved)",
            });
        }
        return rules;
    }

    public void Build(UiPanel panel, PickedElement element, string rulePath)
    {
        _panel = panel;
        var previous = _element;
        _element = element;
        var isTmp = element != null && (element.Capabilities & ElementCapabilities.TmpText) != 0;
        _showTmp = element == null || isTmp;
        _showLegacy = element == null || !isTmp;
        // One the game destroyed since (a closed tooltip) keeps the values read while it was alive.
        _current = element == null ? default
            : element.IsAlive ? ReadCurrentValues(isTmp)
            : element.IsSameAs(previous) ? _current : default;

        // Exact match only - a wildcard resizer that merely happens to affect this element is
        // never loaded as the editable working copy, or Save would edit/rename the shared
        // wildcard instead of creating a rule for this one element. The matching wildcard (if
        // any) is kept separately in _matchingWildcard so RenderInfo can still mention it.
        TextResizerContract existing = null;
        if (rulePath != null)
            TextResizerService.Resizers.TryGetValue(rulePath, out existing);
        if (existing == null && element != null)
            TextResizerService.Resizers.TryGetValue(element.Path, out existing);

        _matchingWildcard = existing == null && element != null
            ? TextResizerService.FindAppropriateResizer(element.Path)
            : null;

        if (existing != null)
        {
            _working = existing with { };
            _savedPath = TextResizerService.ResizerSourceFiles.ContainsKey(existing.Path) ? existing.Path : null;
        }
        else if (element == null)
        {
            _working = null;
            _savedPath = null;
            _dirty = false;
            _hasPreview = false;
            panel.Clear();
            panel.Label($"The resizer '{rulePath}' no longer exists.", 0, 0, panel.Width, 40, 13, TextAnchor.UpperLeft, UiPanel.DimTextColor);
            return;
        }
        else
        {
            _working = new TextResizerContract
            {
                Path = element.Path,
                SampleText = PickedElement.Truncate((element.TextPreview ?? string.Empty).Replace('\n', ' '), 60),
            };
            _savedPath = null;
        }

        _previewPath = _working.Path;
        _dirty = false;
        _hasPreview = false;
        Render();
    }

    public void Tick()
    {
        if (_working != null && _dirty && Time.realtimeSinceStartup - _lastPreviewTime >= PreviewInterval)
            Preview();
    }

    public void Leave()
    {
        if (_working == null || (!_dirty && !_hasPreview))
            return;

        var worthSaving = _savedPath != null || HasAnySetting(_working);
        if (EditorSettings.AutoSave && worthSaving && !string.IsNullOrEmpty(_working.Path))
        {
            SaveCore();
            EditorWindow.SetStatus($"Auto-saved resizer '{_working.Path}' to {SourceFileName(_working.Path)}.");
            return;
        }

        DiscardCore();
        EditorWindow.SetStatus(worthSaving && string.IsNullOrEmpty(_working.Path)
            ? "Resizer changes discarded: it had no path."
            : "Unsaved resizer changes discarded.", warning: worthSaving);
    }

    /// <summary>Rebuilds every widget. Only for structural changes; toggles, choices and step
    /// buttons update their own button in place.</summary>
    private void Render()
    {
        _panel.Clear();
        _stepButtons.Clear();
        var width = _panel.Width;
        var y = 0f;

        _panel.Label("Resizer path", 0, y, LabelWidth, RowHeight);
        _panel.Input(_working.Path, LabelWidth, y, width - LabelWidth, RowHeight, text =>
        {
            _working.Path = text.Trim();
            MarkDirty();
        });
        y += RowStep;

        y = RenderInfo(y, width);

        _panel.Label("Sample text", 0, y, LabelWidth, RowHeight);
        _panel.Input(_working.SampleText, LabelWidth, y, width - LabelWidth, RowHeight, text =>
        {
            _working.SampleText = text;
            MarkDirty();
        }, "reminder of what this resizer is for");
        y += RowStep + 4f;

        // Font size: either an ideal size, or a percentage of the original (min/max for auto size).
        var col = (width - LabelWidth) / 4f;
        _panel.Label("Font size", 0, y, LabelWidth, RowHeight);
        FloatField("Ideal", LabelWidth, y, col, _working.IdealFontSize, v => _working.IdealFontSize = v, Current(_current.FontSize));
        FloatField("%", LabelWidth + col, y, col, _working.FontPercentage, v => _working.FontPercentage = v, "e.g. 0.8");
        FloatField("Min", LabelWidth + 2 * col, y, col, _working.MinFontSize, v => _working.MinFontSize = v, Current(_current.MinSize));
        FloatField("Max", LabelWidth + 3 * col, y, col, _working.MaxFontSize, v => _working.MaxFontSize = v, Current(_current.MaxSize));
        y += RowStep;

        _panel.Label("Spacing", 0, y, LabelWidth, RowHeight);
        FloatField("Line", LabelWidth, y, col, _working.LineSpacing, v => _working.LineSpacing = v, Current(_current.LineSpacing));
        if (_showTmp)
        {
            // Legacy Text has no character/word spacing.
            FloatField("Char", LabelWidth + col, y, col, _working.CharacterSpacing, v => _working.CharacterSpacing = v, Current(_current.CharacterSpacing));
            FloatField("Word", LabelWidth + 2 * col, y, col, _working.WordSpacing, v => _working.WordSpacing = v, Current(_current.WordSpacing));
        }
        y += RowStep + 4f;

        var half = (width - LabelWidth - Gap) / 2f;
        _panel.Label("Alignment", 0, y, LabelWidth, RowHeight);
        ChoiceButton(LabelWidth, y, half, "Alignment", () => _working.Alignment, AlignmentOptions, v => _working.Alignment = v);
        _panel.Label("Overflow", LabelWidth + half + Gap, y, 64, RowHeight);
        ChoiceButton(LabelWidth + half + Gap + 64, y, half - 64, "Overflow", () => _working.OverflowMode, OverflowOptions, v => _working.OverflowMode = v);
        y += RowStep;

        var third = (width - 2 * Gap) / 3f;
        TriToggle("Word wrap", 0, y, third, () => _working.AllowWordWrap, v => _working.AllowWordWrap = v);
        TriToggle("Auto size", third + Gap, y, third, () => _working.AllowAutoSizing, v => _working.AllowAutoSizing = v);
        UiButton trimButton = null;
        trimButton = _panel.Button(TrimText(), 2 * (third + Gap), y, third, RowHeight, () =>
        {
            _working.AllowLeftTrimText = !_working.AllowLeftTrimText;
            MarkDirty();
            trimButton.Set(TrimText(), _working.AllowLeftTrimText ? UiPanel.ButtonColor : UiPanel.MutedButtonColor);
        }, _working.AllowLeftTrimText ? UiPanel.ButtonColor : UiPanel.MutedButtonColor);
        y += RowStep + 4f;

        y = RenderAdjustRows(y);
        y += 6f;

        _panel.Button("Save", 0, y, 110, 28, Save);
        _panel.Button("Discard changes", 118, y, 140, 28, Discard, UiPanel.MutedButtonColor);
        if (_savedPath != null)
            _panel.Button("Delete resizer", width - 120, y, 120, 28, Delete, UiPanel.DangerColor);
    }

    private float RenderInfo(float y, float width)
    {
        string info;
        var color = UiPanel.DimTextColor;
        var isWildcard = _element != null && _savedPath != null && _savedPath != _element.Path;

        if (_element == null)
        {
            info = $"Not on screen - editing the resizer from {SourceFileName(_savedPath)} without a live element.";
            color = UiPanel.WarningColor;
        }
        else if (_savedPath == null)
        {
            info = "New resizer - Save writes it to zzAddedResizers.yaml.";
            if (_matchingWildcard != null)
            {
                info += $" Currently falls back to wildcard resizer '{_matchingWildcard.Path}' - " +
                        "saving here overrides it for just this element.";
                color = UiPanel.WarningColor;
            }
        }
        else if (isWildcard)
        {
            info = $"Editing wildcard resizer from {SourceFileName(_savedPath)} - changes affect every element it matches.";
            color = UiPanel.WarningColor;
        }
        else
            info = $"Resizer from {SourceFileName(_savedPath)}.";

        var layout = _element == null ? null : LayoutApplier.Repository?.Find(_element.Path);
        if (layout != null && (layout.AnchoredPosition != null || layout.OffsetPosition != null
            || layout.SizeDelta != null || layout.OffsetSize != null))
        {
            info += " A layout rule also moves/resizes this element - prefer that over Adjust X/Y/W/H here.";
            color = UiPanel.WarningColor;
        }

        var hasFallback = _savedPath == null && _matchingWildcard != null;
        var infoWidth = isWildcard || hasFallback ? width - 150f : width;
        _panel.Label(info, 0, y, infoWidth, 34, 12, TextAnchor.UpperLeft, color);
        if (hasFallback)
        {
            var wildcardPath = _matchingWildcard.Path;
            _panel.Button("Edit wildcard", width - 142, y, 142, RowHeight,
                () => EditorWindow.OpenRuleForSelected(wildcardPath), UiPanel.MutedButtonColor);
        }
        else if (isWildcard)
        {
            _panel.Button("This element only", width - 142, y, 142, RowHeight, () =>
            {
                _working = _working with { Path = _element.Path };
                _savedPath = null;
                MarkDirty();
                Render();
            }, UiPanel.MutedButtonColor);
        }

        return y + 38f;
    }

    private float RenderAdjustRows(float y)
    {
        _panel.Label("Adjust position", 0, y, LabelWidth, RowHeight);
        _adjustX = AdjustInput(LabelWidth, y, _working.AdjustX, v => _working.AdjustX = v);
        _adjustY = AdjustInput(LabelWidth + FieldWidth + Gap, y, _working.AdjustY, v => _working.AdjustY = v);
        var nudgeX = LabelWidth + 2 * (FieldWidth + Gap) + 6f;
        NudgeButtons(nudgeX, y, "X", "Y", (axis, delta) =>
        {
            if (axis == 0) { _working.AdjustX += delta; _panel.SetInputText(_adjustX, Format(_working.AdjustX)); }
            else { _working.AdjustY += delta; _panel.SetInputText(_adjustY, Format(_working.AdjustY)); }
            MarkDirty();
        });
        y += RowStep;

        _panel.Label("Adjust size", 0, y, LabelWidth, RowHeight);
        _adjustWidth = AdjustInput(LabelWidth, y, _working.AdjustWidth, v => _working.AdjustWidth = v);
        _adjustHeight = AdjustInput(LabelWidth + FieldWidth + Gap, y, _working.AdjustHeight, v => _working.AdjustHeight = v);
        NudgeButtons(nudgeX, y, "W", "H", (axis, delta) =>
        {
            if (axis == 0) { _working.AdjustWidth += delta; _panel.SetInputText(_adjustWidth, Format(_working.AdjustWidth)); }
            else { _working.AdjustHeight += delta; _panel.SetInputText(_adjustHeight, Format(_working.AdjustHeight)); }
            MarkDirty();
        });
        return y + RowStep;
    }

    private InputField AdjustInput(float x, float y, float value, Action<float> set)
    {
        return _panel.Input(value == 0f ? string.Empty : Format(value), x, y, FieldWidth, RowHeight, text =>
        {
            if (string.IsNullOrWhiteSpace(text)) { set(0f); MarkDirty(); }
            else if (TryParse(text, out var parsed)) { set(parsed); MarkDirty(); }
            else EditorWindow.SetStatus($"'{text}' is not a number.", warning: true);
        }, "0");
    }

    private void NudgeButtons(float x, float y, string xLabel, string yLabel, Action<int, float> nudge)
    {
        const float w = 34f;
        _panel.Button(xLabel + "-", x, y, w, RowHeight, () => nudge(0, -NudgeSteps[_nudgeIndex]), UiPanel.MutedButtonColor);
        _panel.Button(xLabel + "+", x + (w + 2), y, w, RowHeight, () => nudge(0, NudgeSteps[_nudgeIndex]), UiPanel.MutedButtonColor);
        _panel.Button(yLabel + "-", x + 2 * (w + 2), y, w, RowHeight, () => nudge(1, -NudgeSteps[_nudgeIndex]), UiPanel.MutedButtonColor);
        _panel.Button(yLabel + "+", x + 3 * (w + 2), y, w, RowHeight, () => nudge(1, NudgeSteps[_nudgeIndex]), UiPanel.MutedButtonColor);
        _stepButtons.Add(_panel.Button(StepText(), x + 4 * (w + 2) + 4, y, 60, RowHeight, () =>
        {
            _nudgeIndex = (_nudgeIndex + 1) % NudgeSteps.Length;
            foreach (var button in _stepButtons)
                button.SetText(StepText());
        }, UiPanel.MutedButtonColor));
    }

    private string StepText() => $"step {NudgeSteps[_nudgeIndex]}";

    private string TrimText() => $"Trim leading space: {(_working.AllowLeftTrimText ? "On" : "Off")}";

    private void FloatField(string label, float x, float y, float width, float? value, Action<float?> set, string placeholder)
    {
        const float labelWidth = 34f;
        _panel.Label(label, x, y, labelWidth, RowHeight, 12, TextAnchor.MiddleRight, UiPanel.DimTextColor);
        _panel.Input(value.HasValue ? Format(value.Value) : string.Empty, x + labelWidth + 3, y, width - labelWidth - 7, RowHeight, text =>
        {
            if (string.IsNullOrWhiteSpace(text)) { set(null); MarkDirty(); }
            else if (TryParse(text, out var parsed)) { set(parsed); MarkDirty(); }
            else EditorWindow.SetStatus($"'{text}' is not a number.", warning: true);
        }, placeholder);
    }

    // Options are only built when the popup opens (and cached), not on every render.
    private void ChoiceButton(float x, float y, float width, string title, Func<string> get, Func<IList<string>> options, Action<string> set)
    {
        var value = get();
        UiButton button = null;
        button = _panel.Button(ChoiceText(value), x, y, width, RowHeight, () => EditorWindow.ShowChoice(title, options(), get(), picked =>
        {
            set(picked ?? string.Empty);
            MarkDirty();
            var updated = get();
            button.Set(ChoiceText(updated), string.IsNullOrEmpty(updated) ? UiPanel.MutedButtonColor : UiPanel.ButtonColor);
        }), string.IsNullOrEmpty(value) ? UiPanel.MutedButtonColor : UiPanel.ButtonColor);
    }

    private static string ChoiceText(string value) => string.IsNullOrEmpty(value) ? "(game default)" : value;

    private void TriToggle(string label, float x, float y, float width, Func<bool?> get, Action<bool?> set)
    {
        var value = get();
        UiButton button = null;
        button = _panel.Button(TriText(label, value), x, y, width, RowHeight, () =>
        {
            var current = get();
            set(current == null ? true : current.Value ? false : (bool?)null);
            MarkDirty();
            var updated = get();
            button.Set(TriText(label, updated), updated == null ? UiPanel.MutedButtonColor : UiPanel.ButtonColor);
        }, value == null ? UiPanel.MutedButtonColor : UiPanel.ButtonColor);
    }

    private static string TriText(string label, bool? value) => $"{label}: {(value == null ? "–" : value.Value ? "On" : "Off")}";

    private void MarkDirty() => _dirty = true;

    private void Preview()
    {
        var patterns = new List<string> { _working.Path };
        if (_previewPath != _working.Path)
        {
            Service.DiscardPreview(_previewPath);
            patterns.Add(_previewPath);
        }

        // No full ApplyAllResizers here: RefreshAffected below re-applies everything the edit
        // can have changed, and this runs every PreviewInterval while editing.
        if (!string.IsNullOrEmpty(_working.Path))
            Service.PreviewResizer(_working with { }, applyAll: false);

        _previewPath = _working.Path;
        _hasPreview = true;
        _dirty = false;
        _lastPreviewTime = Time.realtimeSinceStartup;

        RefreshAffected(patterns);
        EditorWindow.SetStatus("Previewing - Save to keep these changes.");
    }

    // RefreshMatching, hinted with the selected element so an exact-path resizer doesn't rescan
    // every text on screen. Falls back to the full refresh if the hinted one fails.
    private void RefreshAffected(List<string> patterns)
    {
        try
        {
            RefreshHinted(patterns);
        }
        catch (Exception)
        {
            Service.RefreshMatching(patterns);
        }
    }

    // TMP types live in their own method so a game without TextMeshPro only fails this call.
    private void RefreshHinted(List<string> patterns)
    {
        TextMeshProUGUI tmpHint = null;
        Text legacyHint = null;
        if (_element != null && _element.IsAlive)
        {
            if ((_element.Capabilities & ElementCapabilities.TmpText) != 0)
                tmpHint = UiCompat.GetComponent<TextMeshProUGUI>(_element.RectTransform);
            else
                legacyHint = UiCompat.GetComponent<Text>(_element.RectTransform);
        }

        Service.RefreshMatching(patterns, tmpHint, legacyHint);
    }

    private void Save()
    {
        if (string.IsNullOrEmpty(_working.Path))
        {
            EditorWindow.SetStatus("The resizer needs a path.", warning: true);
            return;
        }

        var savedBefore = _savedPath;
        SaveCore();
        EditorWindow.SetStatus($"Saved to {SourceFileName(_working.Path)}.");
        // The info line and Delete button depend on which resizer is saved; nothing else changes.
        if (savedBefore != _savedPath)
            Render();
    }

    private void SaveCore()
    {
        var patterns = new List<string> { _working.Path, _savedPath, _previewPath };
        if (_previewPath != _working.Path)
            Service.DiscardPreview(_previewPath);

        // RefreshMatching below re-applies what the save can have changed, so no ApplyAllResizers
        // rescan as well. Saves are rare, so it's a full rescan: elements the hooks haven't seen
        // since the cache was reset (which a hinted refresh would miss) still get the rule.
        Service.SaveResizer(_working with { }, _savedPath, applyAll: false);
        _savedPath = _working.Path;
        _previewPath = _working.Path;
        _dirty = false;
        _hasPreview = false;

        Service.RefreshMatching(patterns);
    }

    private void Discard()
    {
        DiscardCore();
        Build(_panel, _element, _element == null ? _savedPath : null);
        EditorWindow.SetStatus("Reloaded the resizer from disk.");
    }

    private void DiscardCore()
    {
        var patterns = new List<string> { _working.Path, _savedPath, _previewPath };
        Service.LoadResizers();
        Service.RefreshMatching(patterns);
        _dirty = false;
        _hasPreview = false;
    }

    private void Delete()
    {
        var patterns = new List<string> { _savedPath, _previewPath, _working.Path };
        Service.DeleteResizer(_savedPath);
        Service.DiscardPreview(_previewPath);
        Service.DiscardPreview(_working.Path);
        Service.RefreshMatching(patterns);
        EditorWindow.SetStatus($"Deleted resizer '{_savedPath}'.");
        Build(_panel, _element, _element == null ? _savedPath : null);
    }

    private static string SourceFileName(string path)
    {
        return path != null && TextResizerService.ResizerSourceFiles.TryGetValue(path, out var file)
            ? Path.GetFileName(file)
            : "zzAddedResizers.yaml";
    }

    private static bool HasAnySetting(TextResizerContract c)
    {
        return c.IdealFontSize != null || c.FontPercentage != null || c.MinFontSize != null || c.MaxFontSize != null
            || !string.IsNullOrEmpty(c.Alignment) || !string.IsNullOrEmpty(c.OverflowMode)
            || c.AllowWordWrap != null || c.AllowAutoSizing != null || c.AllowLeftTrimText
            || c.AdjustX != 0 || c.AdjustY != 0 || c.AdjustWidth != 0 || c.AdjustHeight != 0
            || c.LineSpacing != null || c.CharacterSpacing != null || c.WordSpacing != null;
    }

    // The option names TextResizerService understands for this element's text type.
    // Cached per text-type combination: Enum.GetNames + Distinct is wasted work to repeat.
    private int OptionsCacheIndex => (_showTmp ? 2 : 0) + (_showLegacy ? 1 : 0);

    private IList<string> AlignmentOptions() =>
        AlignmentOptionsCache[OptionsCacheIndex] ??= BuildAlignmentOptions();

    private IList<string> OverflowOptions() =>
        OverflowOptionsCache[OptionsCacheIndex] ??= BuildOverflowOptions();

    private IList<string> BuildAlignmentOptions()
    {
        var names = new List<string> { string.Empty };
        if (_showTmp) names.AddRange(TmpAlignmentNames());
        if (_showLegacy) names.AddRange(Enum.GetNames(typeof(TextAnchor)));
        return names.Distinct().ToList();
    }

    private IList<string> BuildOverflowOptions()
    {
        var names = new List<string> { string.Empty };
        if (_showTmp) names.AddRange(TmpOverflowNames());
        if (_showLegacy)
        {
            names.AddRange(Enum.GetNames(typeof(HorizontalWrapMode)));
            names.AddRange(Enum.GetNames(typeof(VerticalWrapMode)));
        }
        return names.Distinct().ToList();
    }

    // TMP types live in their own methods so a game without TextMeshPro only fails these calls.
    private static string[] TmpAlignmentNames() => Enum.GetNames(typeof(TextAlignmentOptions));
    private static string[] TmpOverflowNames() => Enum.GetNames(typeof(TextOverflowModes));

    private CurrentValues ReadCurrentValues(bool isTmp)
    {
        try
        {
            return isTmp ? ReadTmpValues() : ReadLegacyValues();
        }
        catch (Exception)
        {
            return default;
        }
    }

    private CurrentValues ReadTmpValues()
    {
        var text = UiCompat.GetComponent<TMP_Text>(_element.RectTransform);
        return text == null ? default : new CurrentValues
        {
            FontSize = text.fontSize, MinSize = text.fontSizeMin, MaxSize = text.fontSizeMax,
            LineSpacing = text.lineSpacing, CharacterSpacing = text.characterSpacing, WordSpacing = text.wordSpacing,
        };
    }

    private CurrentValues ReadLegacyValues()
    {
        var text = UiCompat.GetComponent<Text>(_element.RectTransform);
        return text == null ? default : new CurrentValues
        {
            FontSize = text.fontSize, MinSize = text.resizeTextMinSize, MaxSize = text.resizeTextMaxSize,
            LineSpacing = text.lineSpacing,
        };
    }

    // Current values as placeholders; blank when there's no element to read them from.
    private string Current(float value) => _element != null ? Format(value) : string.Empty;

    private static string Format(float value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static bool TryParse(string text, out float value) =>
        float.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
}
