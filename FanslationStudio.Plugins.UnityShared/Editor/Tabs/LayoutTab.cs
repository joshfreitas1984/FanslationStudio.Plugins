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

    /// <summary>Save changed rules automatically when leaving the element/tab/window.</summary>
    public static bool AutoSave = true;

    private UiPanel _panel;
    private PickedElement _element;
    private LayoutContract _working;
    private LayoutSnapshot _original;

    // Path of the saved rule being edited (null for a new rule), and the path currently previewed.
    private string _savedPath;
    private string _previewPath;
    private bool _dirty;
    private bool _hasPreview;
    private float _lastPreviewTime;

    private bool _positionIsOffset = true;
    private bool _sizeIsOffset = true;
    private int _nudgeIndex;

    private InputField[] _positionInputs;
    private InputField[] _sizeInputs;

    public string Title => "Layout";

    public bool IsAvailable(PickedElement element) => LayoutApplier.Repository != null;

    public void Build(UiPanel panel, PickedElement element)
    {
        _panel = panel;
        _element = element;
        _original = LayoutApplier.GetOriginal(element.RectTransform);

        var repository = LayoutApplier.Repository;
        var existing = repository.Find(element.Path);
        if (existing != null)
        {
            _working = Clone(existing);
            _savedPath = repository.GetSourceFile(existing.Path) != null ? existing.Path : null;
        }
        else
        {
            _working = new LayoutContract { Path = element.Path };
            _savedPath = null;
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
        if (_dirty && Time.realtimeSinceStartup - _lastPreviewTime >= PreviewInterval)
            Preview();
    }

    public void Leave()
    {
        if (!_dirty && !_hasPreview)
            return;

        // Keep edits to an existing rule, or a new rule that sets something. A new rule with
        // nothing set is just a click-through, so don't litter the files with empty entries.
        var worthSaving = _savedPath != null || HasAnySetting(_working);
        if (AutoSave && worthSaving && !string.IsNullOrEmpty(_working.Path))
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
            || c.ContentSizeFitterEnabled != null || c.LayoutGroupEnabled != null;
    }

    private void Render()
    {
        _panel.Clear();
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
        BoolToggle("Enforce", toggleWidth + Gap, y, toggleWidth, _working.IsEnforced(), v => _working.Enforce = v ? true : (bool?)null);
        BoolToggle("Rule enabled", 2 * (toggleWidth + Gap), y, toggleWidth, _working.IsEnabled(), v => _working.Enabled = v ? (bool?)null : false);
        TriToggle("Copy size", 3 * (toggleWidth + Gap), y, toggleWidth, () => _working.CopySizeFromSource, v => _working.CopySizeFromSource = v);
        y += RowStep + 4f;

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
        var isWildcard = _savedPath != null && _savedPath != _element.Path;

        if (_savedPath == null)
            info = "New rule - Save writes it to zzAddedLayouts.yaml.";
        else if (isWildcard)
        {
            info = $"Editing wildcard rule from {Path.GetFileName(LayoutApplier.Repository.GetSourceFile(_savedPath))} - changes affect every element it matches.";
            color = UiPanel.WarningColor;
        }
        else
            info = $"Rule from {Path.GetFileName(LayoutApplier.Repository.GetSourceFile(_savedPath))}.";

        var parent = _element.RectTransform.parent;
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
        _panel.Button(_positionIsOffset ? "Offset" : "Absolute", LabelWidth - 56, y, 52, RowHeight, TogglePositionMode, UiPanel.MutedButtonColor);

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
        _panel.Button(_sizeIsOffset ? "Offset" : "Absolute", LabelWidth - 56, y, 52, RowHeight, ToggleSizeMode, UiPanel.MutedButtonColor);

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
        _panel.Button($"step {NudgeSteps[_nudgeIndex]}", x + 4 * (w + 2) + 4, y, 60, RowHeight, () =>
        {
            _nudgeIndex = (_nudgeIndex + 1) % NudgeSteps.Length;
            Render();
        }, UiPanel.MutedButtonColor);
    }

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

    private void TriToggle(string label, float x, float y, float width, Func<bool?> get, Action<bool?> set)
    {
        var value = get();
        var text = $"{label}: {(value == null ? "–" : value.Value ? "On" : "Off")}";
        _panel.Button(text, x, y, width, RowHeight, () =>
        {
            // unset -> On -> Off -> unset
            set(value == null ? true : value.Value ? false : (bool?)null);
            MarkDirty();
            Render();
        }, value == null ? UiPanel.MutedButtonColor : UiPanel.ButtonColor);
    }

    private void BoolToggle(string label, float x, float y, float width, bool value, Action<bool> set)
    {
        _panel.Button($"{label}: {(value ? "On" : "Off")}", x, y, width, RowHeight, () =>
        {
            set(!value);
            MarkDirty();
            Render();
        }, value ? UiPanel.ButtonColor : UiPanel.MutedButtonColor);
    }

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

        LayoutApplier.Refresh(patterns, _element.RectTransform);
        EditorWindow.SetStatus("Previewing - Save to keep these changes.");
    }

    private void Save()
    {
        if (string.IsNullOrEmpty(_working.Path))
        {
            EditorWindow.SetStatus("The rule needs a path.", warning: true);
            return;
        }

        var file = SaveCore();
        EditorWindow.SetStatus($"Saved to {Path.GetFileName(file)}.");
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

        LayoutApplier.Refresh(patterns, _element.RectTransform);
        return file;
    }

    private void Discard()
    {
        LayoutApplier.Repository.Load();
        LayoutApplier.ReapplyAll();
        Build(_panel, _element);
        EditorWindow.SetStatus("Reloaded the rule from disk.");
    }

    private void Delete()
    {
        var repository = LayoutApplier.Repository;
        var patterns = new List<string> { _savedPath, _previewPath, _working.Path };
        repository.Delete(_savedPath);
        repository.DiscardPreview(_previewPath);
        repository.DiscardPreview(_working.Path);

        LayoutApplier.Refresh(patterns, _element.RectTransform);
        EditorWindow.SetStatus($"Deleted rule '{_savedPath}'.");
        Build(_panel, _element);
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
