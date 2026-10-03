using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using FanslationStudio.Plugins.Layout;
using FanslationStudio.Plugins.UnityShared.Editor.Ui;
using FanslationStudio.Plugins.UnityShared.Layout;
using UnityEngine;
using UnityEngine.UI;

namespace FanslationStudio.Plugins.UnityShared.Editor.Tabs;

/// <summary>
/// Edits the layout rule for the selected element. Edits preview live (throttled); Save writes the
/// rule to the file that owns it (zzAddedLayouts.yaml for new rules); leaving without saving
/// reloads the rules from disk so previews never linger.
/// </summary>
internal sealed class LayoutTab : IEditorTab
{
    private const float RowHeight = 24f;
    private const float RowStep = 28f;
    private const float LabelWidth = 104f;
    private const float FieldWidth = 64f;
    private const float Gap = 4f;
    private const float PreviewInterval = 0.12f;
    private static readonly float[] NudgeSteps = { 1f, 5f, 10f, 50f };

    private UiPanel _panel;
    private PickedElement _element;
    private LayoutContract _working;
    private LayoutSnapshot _original;

    // Path of the saved rule being edited (null for a new rule), and the path currently previewed.
    private string _savedPath;
    private string _previewPath;
    // A wildcard rule this element falls back to when it has no rule of its own - shown as a note
    // in RenderRuleInfo, never loaded into _working (see Build()).
    private LayoutContract _matchingWildcard;
    private bool _dirty;
    private bool _hasPreview;
    private float _lastPreviewTime;

    private bool _positionIsOffset = true;
    private bool _sizeIsOffset = true;
    private int _nudgeIndex;

    private InputField[] _positionInputs;
    private InputField[] _sizeInputs;
    // Both rows' "step" buttons share _nudgeIndex, so both relabel when either is clicked.
    private readonly List<UiButton> _stepButtons = new List<UiButton>();

    public string Title => "Layout";

    public bool IsAvailable(PickedElement element) => LayoutApplier.Repository != null;

    public string EditingRulePath => _savedPath;

    public int RulesVersion => LayoutApplier.Repository?.Version ?? 0;

    public IReadOnlyList<RuleSummary> ListRules()
    {
        var repository = LayoutApplier.Repository;
        var rules = new List<RuleSummary>();
        if (repository == null)
            return rules;

        foreach (var rule in repository.All)
        {
            rules.Add(new RuleSummary
            {
                Key = rule.Path,
                Path = rule.Path,
                Description = rule.Name,
                File = Path.GetFileName(repository.GetSourceFile(rule.Path) ?? "(unsaved)"),
            });
        }
        return rules;
    }

    public void Build(UiPanel panel, PickedElement element, string rulePath)
    {
        _panel = panel;
        var previous = _element;
        _element = element;
        // Without an element on screen there are no originals; blank components mean 0. One the
        // game destroyed since (a closed tooltip) keeps the originals read while it was alive.
        _original = element == null ? default
            : element.IsAlive ? LayoutApplier.GetOriginal(element.RectTransform)
            : element.IsSameAs(previous) ? _original : default;

        // Exact match only - a wildcard rule that merely happens to affect this element is never
        // loaded as the editable working copy, or Save would edit/rename the shared wildcard
        // instead of creating a rule for this one element.
        var repository = LayoutApplier.Repository;
        var existing = rulePath != null ? repository.Get(rulePath) : null;
        if (existing == null && element != null)
            existing = repository.Get(element.Path);

        _matchingWildcard = existing == null && element != null ? repository.Find(element.Path) : null;

        if (existing != null)
        {
            _working = Clone(existing);
            _savedPath = repository.GetSourceFile(existing.Path) != null ? existing.Path : null;
        }
        else if (element != null)
        {
            _working = new LayoutContract { Path = element.Path };
            _savedPath = null;
        }
        else
        {
            _working = null;
            _savedPath = null;
            _dirty = false;
            _hasPreview = false;
            panel.Clear();
            panel.Label($"The rule '{rulePath}' no longer exists.", 0, 0, panel.Width, 40, 13, TextAnchor.UpperLeft, UiPanel.DimTextColor);
            return;
        }

        // Edit position/size in one mode at a time: fold an offset into an absolute value.
        if (_working.AnchoredPosition != null && _working.OffsetPosition != null)
        {
            _working.AnchoredPosition = Add(_working.AnchoredPosition, _working.OffsetPosition);
            _working.OffsetPosition = null;
        }
        if (_working.SizeDelta != null && _working.OffsetSize != null)
        {
            _working.SizeDelta = Add(_working.SizeDelta, _working.OffsetSize);
            _working.OffsetSize = null;
        }
        _positionIsOffset = _working.AnchoredPosition == null;
        _sizeIsOffset = _working.SizeDelta == null;

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

        // Keep edits to an existing rule, or a new rule that sets something. A new rule with
        // nothing set is just a click-through, so don't litter the files with empty entries.
        var worthSaving = _savedPath != null || HasAnySetting(_working);
        if (EditorSettings.AutoSave && worthSaving && !string.IsNullOrEmpty(_working.Path))
        {
            var file = SaveCore();
            EditorWindow.SetStatus($"Auto-saved '{_working.Path}' to {Path.GetFileName(file)}.");
            return;
        }

        LayoutApplier.Repository.Load();
        LayoutApplier.ReapplyAll();
        _dirty = false;
        _hasPreview = false;
        EditorWindow.SetStatus(worthSaving && string.IsNullOrEmpty(_working.Path)
            ? "Layout changes discarded: the rule had no path."
            : "Unsaved layout changes discarded.", warning: worthSaving);
    }

    private static bool HasAnySetting(LayoutContract c)
    {
        return c.Enabled != null || c.Enforce != null || c.Active != null
            || c.AnchorMin != null || c.AnchorMax != null || c.Pivot != null
            || c.AnchoredPosition != null || c.OffsetPosition != null || c.SizeDelta != null || c.OffsetSize != null
            || c.LocalPosition != null || c.LocalScale != null || c.RotationZ != null
            || c.CopyRectFrom != null || c.CopySizeFromSource != null || c.PlaceBefore != null
            || c.ImageEnabled != null || c.PreserveAspect != null
            || c.ContentSizeFitterEnabled != null || c.LayoutGroupEnabled != null
            || c.CounterRotateChildren != null || c.CounterRotateSwapSize != null
            || c.ContentSizeFitterHorizontal != null || c.ContentSizeFitterVertical != null
            || c.LayoutGroupChildControlWidth != null || c.LayoutGroupChildControlHeight != null
            || c.LayoutGroupChildForceExpandWidth != null || c.LayoutGroupChildForceExpandHeight != null
            || c.LayoutGroupSpacing != null;
    }

    /// <summary>Rebuilds every widget. Only for structural changes (different fields or modes);
    /// toggles and step buttons update their own button in place.</summary>
    private void Render()
    {
        _panel.Clear();
        _stepButtons.Clear();
        var width = _panel.Width;
        var y = 0f;

        _panel.Label("Rule path", 0, y, LabelWidth, RowHeight);
        _panel.Input(_working.Path, LabelWidth, y, width - LabelWidth, RowHeight, text =>
        {
            _working.Path = text.Trim();
            MarkDirty();
        });
        y += RowStep;

        y = RenderRuleInfo(y, width);

        _panel.Label("Name", 0, y, LabelWidth, RowHeight);
        _panel.Input(_working.Name, LabelWidth, y, width - LabelWidth, RowHeight, text =>
        {
            _working.Name = string.IsNullOrWhiteSpace(text) ? null : text;
            MarkDirty();
        }, "optional description");
        y += RowStep + 4f;

        y = RenderPositionRow(y);
        y = RenderSizeRow(y);

        y = VectorRow("Anchor min", y, 2, () => _working.AnchorMin, v => _working.AnchorMin = v, ToArray(_original.AnchorMin), false);
        y = VectorRow("Anchor max", y, 2, () => _working.AnchorMax, v => _working.AnchorMax = v, ToArray(_original.AnchorMax), false);
        y = VectorRow("Pivot", y, 2, () => _working.Pivot, v => _working.Pivot = v, ToArray(_original.Pivot), false);
        y = VectorRow("Local position", y, 3, () => _working.LocalPosition, v => _working.LocalPosition = v, ToArray(_original.LocalPosition), false);
        y = VectorRow("Local scale", y, 3, () => _working.LocalScale, v => _working.LocalScale = v, ToArray(_original.LocalScale), false);

        _panel.Label("Rotation Z", 0, y, LabelWidth, RowHeight);
        _panel.Input(Format(_working.RotationZ), LabelWidth, y, FieldWidth, RowHeight, text =>
        {
            if (TryParseOptional(text, out var value))
            {
                _working.RotationZ = value;
                MarkDirty();
            }
        }, Format(_original.LocalEuler.z));
        y += RowStep + 4f;

        var toggleWidth = (width - 3 * Gap) / 4f;
        TriToggle("Active", 0, y, toggleWidth, () => _working.Active, v => _working.Active = v);
        TriToggle("Image", toggleWidth + Gap, y, toggleWidth, () => _working.ImageEnabled, v => _working.ImageEnabled = v);
        TriToggle("Keep aspect", 2 * (toggleWidth + Gap), y, toggleWidth, () => _working.PreserveAspect, v => _working.PreserveAspect = v);
        TriToggle("Size fitter", 3 * (toggleWidth + Gap), y, toggleWidth, () => _working.ContentSizeFitterEnabled, v => _working.ContentSizeFitterEnabled = v);
        y += RowStep;
        TriToggle("Layout group", 0, y, toggleWidth, () => _working.LayoutGroupEnabled, v => _working.LayoutGroupEnabled = v);
        BoolToggle("Enforce", toggleWidth + Gap, y, toggleWidth, () => _working.IsEnforced(), v => _working.Enforce = v ? true : (bool?)null);
        BoolToggle("Rule enabled", 2 * (toggleWidth + Gap), y, toggleWidth, () => _working.IsEnabled(), v => _working.Enabled = v ? (bool?)null : false);
        TriToggle("Copy size", 3 * (toggleWidth + Gap), y, toggleWidth, () => _working.CopySizeFromSource, v => _working.CopySizeFromSource = v);
        y += RowStep;
        BoolToggle("Counter-rotate children", 0, y, toggleWidth * 2 + Gap, () => _working.CounterRotateChildren == true,
            v => _working.CounterRotateChildren = v ? true : (bool?)null);
        BoolToggle("Swap child size", 2 * (toggleWidth + Gap), y, toggleWidth * 2 + Gap, () => _working.CounterRotateSwapSize == true,
            v => _working.CounterRotateSwapSize = v ? true : (bool?)null);
        y += RowStep + 4f;

        y = RenderFitterAndGroupRows(y, width);

        y = StringRow("Copy rect from", y, width, () => _working.CopyRectFrom, v => _working.CopyRectFrom = v, "e.g. ../HeroName");
        y = StringRow("Place before", y, width, () => _working.PlaceBefore, v => _working.PlaceBefore = v, "sibling, e.g. ../HeroName");
        y += 6f;

        _panel.Button("Save", 0, y, 110, 28, Save);
        _panel.Button("Discard changes", 118, y, 140, 28, Discard, UiPanel.MutedButtonColor);
        if (_savedPath != null)
            _panel.Button("Delete rule", width - 110, y, 110, 28, Delete, UiPanel.DangerColor);
    }

    private float RenderRuleInfo(float y, float width)
    {
        string info;
        var color = UiPanel.DimTextColor;
        var isWildcard = _element != null && _savedPath != null && _savedPath != _element.Path;

        if (_element == null)
        {
            info = $"Not on screen - editing the rule from {Path.GetFileName(LayoutApplier.Repository.GetSourceFile(_savedPath) ?? "?")} " +
                   "without a live element. Blank vector components count as 0.";
            color = UiPanel.WarningColor;
        }
        else if (_savedPath == null)
        {
            info = "New rule - Save writes it to zzAddedLayouts.yaml.";
            if (_matchingWildcard != null)
            {
                info += $" Currently falls back to wildcard rule '{_matchingWildcard.Path}' - " +
                        "saving here overrides it for just this element.";
                color = UiPanel.WarningColor;
            }
        }
        else if (isWildcard)
        {
            info = $"Editing wildcard rule from {Path.GetFileName(LayoutApplier.Repository.GetSourceFile(_savedPath))} - changes affect every element it matches.";
            color = UiPanel.WarningColor;
        }
        else
            info = $"Rule from {Path.GetFileName(LayoutApplier.Repository.GetSourceFile(_savedPath))}.";

        var parent = _element != null && _element.IsAlive ? _element.RectTransform.parent : null;
        var parentGroup = parent == null ? null : UiCompat.GetComponent<LayoutGroup>(parent);
        if (parentGroup != null && parentGroup.enabled)
        {
            info += " Parent has a layout group, which may override position/size (set Layout group: Off on the parent).";
            color = UiPanel.WarningColor;
        }

        var infoWidth = isWildcard ? width - 150f : width;
        _panel.Label(info, 0, y, infoWidth, 34, 12, TextAnchor.UpperLeft, color);
        if (isWildcard)
        {
            _panel.Button("This element only", width - 142, y, 142, RowHeight, () =>
            {
                _working = Clone(_working);
                _working.Path = _element.Path;
                _savedPath = null;
                MarkDirty();
                Render();
            }, UiPanel.MutedButtonColor);
        }

        return y + 38f;
    }

    private float RenderPositionRow(float y)
    {
        _panel.Label("Position", 0, y, LabelWidth - 58, RowHeight);
        // Converting between modes needs the element's original value, so only with an element.
        _panel.Button(_positionIsOffset ? "Offset" : "Absolute", LabelWidth - 56, y, 52, RowHeight,
            _element != null ? TogglePositionMode : (Action)null, UiPanel.MutedButtonColor);

        var value = _positionIsOffset ? _working.OffsetPosition : _working.AnchoredPosition;
        var fallback = _positionIsOffset ? new[] { 0f, 0f } : ToArray(_original.AnchoredPosition);
        _positionInputs = VectorInputs(LabelWidth, y, 2, value, fallback, v =>
        {
            if (_positionIsOffset) _working.OffsetPosition = v; else _working.AnchoredPosition = v;
        });

        var x = LabelWidth + 2 * (FieldWidth + Gap) + 6f;
        NudgeButtons(x, y, "X", "Y", (axis, delta) => Nudge(ref _working.OffsetPosition, ref _working.AnchoredPosition,
            _positionIsOffset, ToArray(_original.AnchoredPosition), axis, delta, _positionInputs));
        return y + RowStep;
    }

    private float RenderSizeRow(float y)
    {
        _panel.Label("Size", 0, y, LabelWidth - 58, RowHeight);
        _panel.Button(_sizeIsOffset ? "Offset" : "Absolute", LabelWidth - 56, y, 52, RowHeight,
            _element != null ? ToggleSizeMode : (Action)null, UiPanel.MutedButtonColor);

        var value = _sizeIsOffset ? _working.OffsetSize : _working.SizeDelta;
        var fallback = _sizeIsOffset ? new[] { 0f, 0f } : ToArray(_original.SizeDelta);
        _sizeInputs = VectorInputs(LabelWidth, y, 2, value, fallback, v =>
        {
            if (_sizeIsOffset) _working.OffsetSize = v; else _working.SizeDelta = v;
        });

        var x = LabelWidth + 2 * (FieldWidth + Gap) + 6f;
        NudgeButtons(x, y, "W", "H", (axis, delta) => Nudge(ref _working.OffsetSize, ref _working.SizeDelta,
            _sizeIsOffset, ToArray(_original.SizeDelta), axis, delta, _sizeInputs));
        return y + RowStep;
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

    private void Nudge(ref float[] offset, ref float[] absolute, bool isOffset, float[] original, int axis, float delta, InputField[] inputs)
    {
        float[] value;
        if (isOffset)
        {
            value = offset != null ? (float[])offset.Clone() : new float[2];
            value[axis] += delta;
            offset = value;
        }
        else
        {
            value = absolute != null ? (float[])absolute.Clone() : (float[])original.Clone();
            value[axis] += delta;
            absolute = value;
        }

        for (var i = 0; i < inputs.Length; i++)
            _panel.SetInputText(inputs[i], Format(value[i]));
        MarkDirty();
    }

    private void TogglePositionMode()
    {
        var original = ToArray(_original.AnchoredPosition);
        if (_positionIsOffset && _working.OffsetPosition != null)
        {
            _working.AnchoredPosition = Add(original, _working.OffsetPosition);
            _working.OffsetPosition = null;
        }
        else if (!_positionIsOffset && _working.AnchoredPosition != null)
        {
            _working.OffsetPosition = Subtract(_working.AnchoredPosition, original);
            _working.AnchoredPosition = null;
        }
        _positionIsOffset = !_positionIsOffset;
        MarkDirty();
        Render();
    }

    private void ToggleSizeMode()
    {
        var original = ToArray(_original.SizeDelta);
        if (_sizeIsOffset && _working.OffsetSize != null)
        {
            _working.SizeDelta = Add(original, _working.OffsetSize);
            _working.OffsetSize = null;
        }
        else if (!_sizeIsOffset && _working.SizeDelta != null)
        {
            _working.OffsetSize = Subtract(_working.SizeDelta, original);
            _working.SizeDelta = null;
        }
        _sizeIsOffset = !_sizeIsOffset;
        MarkDirty();
        Render();
    }

    private float VectorRow(string label, float y, int dimensions, Func<float[]> get, Action<float[]> set, float[] original, bool zeroFallback)
    {
        _panel.Label(label, 0, y, LabelWidth, RowHeight);
        VectorInputs(LabelWidth, y, dimensions, get(), zeroFallback ? new float[dimensions] : original, set);
        return y + RowStep;
    }

    /// <summary>
    /// One input per component. All blank means "not set"; a blank component takes the fallback
    /// (the original value for absolute fields, 0 for offsets), which the placeholder shows.
    /// </summary>
    private InputField[] VectorInputs(float x, float y, int dimensions, float[] value, float[] fallback, Action<float[]> set)
    {
        var inputs = new InputField[dimensions];
        for (var i = 0; i < dimensions; i++)
        {
            var text = value != null && value.Length == dimensions ? Format(value[i]) : string.Empty;
            inputs[i] = _panel.Input(text, x + i * (FieldWidth + Gap), y, FieldWidth, RowHeight, _ =>
            {
                if (TryReadVector(inputs, fallback, out var parsed))
                {
                    set(parsed);
                    MarkDirty();
                }
            }, Format(fallback[i]));
        }
        return inputs;
    }

    private static bool TryReadVector(InputField[] inputs, float[] fallback, out float[] value)
    {
        value = null;
        var anySet = false;
        var result = new float[inputs.Length];

        for (var i = 0; i < inputs.Length; i++)
        {
            var text = inputs[i].text;
            if (string.IsNullOrWhiteSpace(text))
            {
                result[i] = fallback[i];
                continue;
            }

            if (!TryParse(text, out result[i]))
            {
                EditorWindow.SetStatus($"'{text}' is not a number.", warning: true);
                return false;
            }
            anySet = true;
        }

        value = anySet ? result : null;
        return true;
    }

    private float StringRow(string label, float y, float width, Func<string> get, Action<string> set, string placeholder)
    {
        _panel.Label(label, 0, y, LabelWidth, RowHeight);
        _panel.Input(get(), LabelWidth, y, width - LabelWidth, RowHeight, text =>
        {
            set(string.IsNullOrWhiteSpace(text) ? null : text.Trim());
            MarkDirty();
        }, placeholder);
        return y + RowStep;
    }

    private static readonly string[] FitModeOptions = { "", "Unconstrained", "MinSize", "PreferredSize" };

    /// <summary>
    /// Editable ContentSizeFitter/Horizontal-or-VerticalLayoutGroup fields, shown directly on the
    /// main Layout form rather than tucked away in the Inspect tab - editing these usually happens
    /// right alongside anchors/size/rotation while chasing a sizing bug, so they belong next to
    /// them. Always shown (like Size fitter/Layout group above) even if the element doesn't
    /// currently have that component; the field is simply a no-op until it does.
    /// </summary>
    private float RenderFitterAndGroupRows(float y, float width)
    {
        _panel.Label("ContentSizeFitter / LayoutGroup (Horizontal/Vertical)", 0, y, width, 16, 11, TextAnchor.MiddleLeft, UiPanel.DimTextColor);
        y += 18f;

        var half = (width - Gap) / 2f;
        FitModeField("horizontalFit", 0, y, half, () => _working.ContentSizeFitterHorizontal, v => _working.ContentSizeFitterHorizontal = v);
        FitModeField("verticalFit", half + Gap, y, half, () => _working.ContentSizeFitterVertical, v => _working.ContentSizeFitterVertical = v);
        y += RowStep;

        var toggleWidth = (width - 3 * Gap) / 4f;
        TriToggle("childControlWidth", 0, y, toggleWidth, () => _working.LayoutGroupChildControlWidth, v => _working.LayoutGroupChildControlWidth = v);
        TriToggle("childControlHeight", toggleWidth + Gap, y, toggleWidth, () => _working.LayoutGroupChildControlHeight, v => _working.LayoutGroupChildControlHeight = v);
        TriToggle("forceExpandWidth", 2 * (toggleWidth + Gap), y, toggleWidth, () => _working.LayoutGroupChildForceExpandWidth, v => _working.LayoutGroupChildForceExpandWidth = v);
        TriToggle("forceExpandHeight", 3 * (toggleWidth + Gap), y, toggleWidth, () => _working.LayoutGroupChildForceExpandHeight, v => _working.LayoutGroupChildForceExpandHeight = v);
        y += RowStep;

        _panel.Label("spacing", 0, y, LabelWidth, RowHeight);
        _panel.Input(Format(_working.LayoutGroupSpacing), LabelWidth, y, FieldWidth, RowHeight, text =>
        {
            if (TryParseOptional(text, out var value))
            {
                _working.LayoutGroupSpacing = value;
                MarkDirty();
            }
        });
        y += RowStep;

        return y;
    }

    private void FitModeField(string label, float x, float y, float width, Func<string> get, Action<string> set)
    {
        var labelWidth = 66f;
        _panel.Label(label, x, y, labelWidth, RowHeight, 11, TextAnchor.MiddleLeft, UiPanel.DimTextColor);
        UiButton button = null;
        button = _panel.Button(FitModeText(get()), x + labelWidth, y, width - labelWidth, RowHeight, () =>
            EditorWindow.ShowChoice(label, FitModeOptions, get() ?? string.Empty, picked =>
            {
                set(string.IsNullOrEmpty(picked) ? null : picked);
                MarkDirty();
                var value = get();
                button.Set(FitModeText(value), string.IsNullOrEmpty(value) ? UiPanel.MutedButtonColor : UiPanel.ButtonColor);
            }), string.IsNullOrEmpty(get()) ? UiPanel.MutedButtonColor : UiPanel.ButtonColor);
    }

    private static string FitModeText(string value) => string.IsNullOrEmpty(value) ? "(default)" : value;

    private void TriToggle(string label, float x, float y, float width, Func<bool?> get, Action<bool?> set)
    {
        var value = get();
        UiButton button = null;
        button = _panel.Button(TriText(label, value), x, y, width, RowHeight, () =>
        {
            // unset -> On -> Off -> unset
            var current = get();
            set(current == null ? true : current.Value ? false : (bool?)null);
            MarkDirty();
            var updated = get();
            button.Set(TriText(label, updated), updated == null ? UiPanel.MutedButtonColor : UiPanel.ButtonColor);
        }, value == null ? UiPanel.MutedButtonColor : UiPanel.ButtonColor);
    }

    private static string TriText(string label, bool? value) => $"{label}: {(value == null ? "–" : value.Value ? "On" : "Off")}";

    private void BoolToggle(string label, float x, float y, float width, Func<bool> get, Action<bool> set)
    {
        var value = get();
        UiButton button = null;
        button = _panel.Button(BoolText(label, value), x, y, width, RowHeight, () =>
        {
            set(!get());
            MarkDirty();
            var updated = get();
            button.Set(BoolText(label, updated), updated ? UiPanel.ButtonColor : UiPanel.MutedButtonColor);
        }, value ? UiPanel.ButtonColor : UiPanel.MutedButtonColor);
    }

    private static string BoolText(string label, bool value) => $"{label}: {(value ? "On" : "Off")}";

    private void MarkDirty()
    {
        _dirty = true;
    }

    private void Preview()
    {
        var repository = LayoutApplier.Repository;
        var patterns = new List<string> { _working.Path };

        if (_previewPath != _working.Path)
        {
            repository.DiscardPreview(_previewPath);
            patterns.Add(_previewPath);
        }

        if (!string.IsNullOrEmpty(_working.Path))
            repository.Preview(Clone(_working));

        _previewPath = _working.Path;
        _hasPreview = true;
        _dirty = false;
        _lastPreviewTime = Time.realtimeSinceStartup;

        LayoutApplier.Refresh(patterns, _element?.RectTransform);
        EditorWindow.SetStatus("Previewing - Save to keep these changes.");
    }

    private void Save()
    {
        if (string.IsNullOrEmpty(_working.Path))
        {
            EditorWindow.SetStatus("The rule needs a path.", warning: true);
            return;
        }

        var savedBefore = _savedPath;
        var file = SaveCore();
        EditorWindow.SetStatus($"Saved to {Path.GetFileName(file)}.");
        // The rule info line and Delete button depend on which rule is saved; nothing else changes.
        if (savedBefore != _savedPath)
            Render();
    }

    private string SaveCore()
    {
        var repository = LayoutApplier.Repository;
        var patterns = new List<string> { _working.Path, _savedPath, _previewPath };
        if (_previewPath != _working.Path)
            repository.DiscardPreview(_previewPath);

        var file = repository.Save(Clone(_working), _savedPath);
        _savedPath = _working.Path;
        _previewPath = _working.Path;
        _dirty = false;
        _hasPreview = false;

        LayoutApplier.Refresh(patterns, _element?.RectTransform);
        return file;
    }

    private void Discard()
    {
        LayoutApplier.Repository.Load();
        LayoutApplier.ReapplyAll();
        Build(_panel, _element, _element == null ? _savedPath : null);
        EditorWindow.SetStatus("Reloaded the rule from disk.");
    }

    private void Delete()
    {
        var repository = LayoutApplier.Repository;
        var patterns = new List<string> { _savedPath, _previewPath, _working.Path };
        repository.Delete(_savedPath);
        repository.DiscardPreview(_previewPath);
        repository.DiscardPreview(_working.Path);

        LayoutApplier.Refresh(patterns, _element?.RectTransform);
        EditorWindow.SetStatus($"Deleted rule '{_savedPath}'.");
        Build(_panel, _element, _element == null ? _savedPath : null);
    }

    private static LayoutContract Clone(LayoutContract source)
    {
        var copy = source with { };
        copy.AnchorMin = CloneArray(source.AnchorMin);
        copy.AnchorMax = CloneArray(source.AnchorMax);
        copy.Pivot = CloneArray(source.Pivot);
        copy.AnchoredPosition = CloneArray(source.AnchoredPosition);
        copy.OffsetPosition = CloneArray(source.OffsetPosition);
        copy.SizeDelta = CloneArray(source.SizeDelta);
        copy.OffsetSize = CloneArray(source.OffsetSize);
        copy.LocalPosition = CloneArray(source.LocalPosition);
        copy.LocalScale = CloneArray(source.LocalScale);
        return copy;
    }

    private static float[] CloneArray(float[] value) => value == null ? null : (float[])value.Clone();

    private static float[] Add(float[] a, float[] b) => new[] { a[0] + b[0], a[1] + b[1] };
    private static float[] Subtract(float[] a, float[] b) => new[] { a[0] - b[0], a[1] - b[1] };
    private static float[] ToArray(Vector2 v) => new[] { v.x, v.y };
    private static float[] ToArray(Vector3 v) => new[] { v.x, v.y, v.z };

    private static string Format(float? value) => value.HasValue ? Format(value.Value) : string.Empty;
    private static string Format(float value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static bool TryParse(string text, out float value)
    {
        return float.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryParseOptional(string text, out float? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(text))
            return true;
        if (!TryParse(text, out var parsed))
        {
            EditorWindow.SetStatus($"'{text}' is not a number.", warning: true);
            return false;
        }
        value = parsed;
        return true;
    }
}
