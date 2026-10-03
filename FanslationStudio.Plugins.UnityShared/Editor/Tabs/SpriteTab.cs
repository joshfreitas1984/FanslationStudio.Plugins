using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using FanslationStudio.Plugins.Sprites;
using FanslationStudio.Plugins.UnityShared.Editor.Ui;
using FanslationStudio.Plugins.UnityShared.Sprites;
using UnityEngine;
using UnityEngine.UI;

namespace FanslationStudio.Plugins.UnityShared.Editor.Tabs;

/// <summary>
/// Dumps and replaces the selected Image's sprite. Workflow: Dump original -> edit the PNG in
/// sprites2/dumped/ -> Reload PNG. Rules live in sprites2/*.yaml, with the same preview/save/
/// discard/delete and auto-save flow as the other tabs.
/// </summary>
internal sealed class SpriteTab : IEditorTab
{
    private const float RowHeight = 24f;
    private const float RowStep = 28f;
    private const float LabelWidth = 118f;
    private const float Gap = 4f;
    private const float ThumbnailSize = 132f;
    private const float PreviewInterval = 0.2f;

    private UiPanel _panel;
    private PickedElement _element;
    private Image _image;
    private Sprite _original;
    private SpriteContract _working;
    private string _savedKey;
    private string _previewKey;
    private bool _dirty;
    private bool _hasPreview;
    private float _lastPreviewTime;

    public string Title => "Sprite";

    public bool IsAvailable(PickedElement element) =>
        SpriteApplier.Repository != null && (element == null || (element.Capabilities & ElementCapabilities.Sprite) != 0);

    public string EditingRulePath => _savedKey;

    public int RulesVersion => SpriteApplier.Repository?.Version ?? 0;

    public IReadOnlyList<RuleSummary> ListRules()
    {
        var rules = new List<RuleSummary>();
        var repository = SpriteApplier.Repository;
        if (repository == null)
            return rules;

        foreach (var rule in repository.All)
        {
            var mapping = string.IsNullOrEmpty(rule.OriginalSprite)
                ? $"→ {rule.ReplacementSprite}"
                : $"{rule.OriginalSprite} → {rule.ReplacementSprite}";
            rules.Add(new RuleSummary
            {
                Key = SpriteContract.KeyOf(rule),
                Path = rule.Path,
                Description = string.IsNullOrEmpty(rule.Name) ? mapping : $"{rule.Name} ({mapping})",
                File = Path.GetFileName(repository.GetSourceFile(SpriteContract.KeyOf(rule)) ?? "(unsaved)"),
            });
        }
        return rules;
    }

    public void Build(UiPanel panel, PickedElement element, string ruleKey)
    {
        _panel = panel;
        _element = element;
        _image = element != null && element.IsAlive ? UiCompat.GetComponent<Image>(element.RectTransform) : null;
        _original = SpriteApplier.GetOriginalSprite(_image);

        var repository = SpriteApplier.Repository;
        var existing = ruleKey != null ? repository.Get(ruleKey) : null;
        if (existing == null && element != null && _original != null)
            existing = FindRuleFor(element.Path, _original.name);

        if (existing != null)
        {
            _working = existing with { };
            _savedKey = repository.GetSourceFile(SpriteContract.KeyOf(existing)) != null ? SpriteContract.KeyOf(existing) : null;
        }
        else if (element != null && _original != null)
        {
            _working = new SpriteContract
            {
                Path = element.Path,
                OriginalSprite = _original.name,
                ReplacementSprite = SpriteContract.SafeFileName(_original.name),
            };
            _savedKey = null;
        }
        else
        {
            _working = null;
            _savedKey = null;
            _dirty = false;
            _hasPreview = false;
            panel.Clear();
            var message = element == null ? $"The sprite rule '{ruleKey}' no longer exists." : "This element has no sprite.";
            panel.Label(message, 0, 0, panel.Width, 40, 13, TextAnchor.UpperLeft, UiPanel.DimTextColor);
            return;
        }

        _previewKey = SpriteContract.KeyOf(_working);
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

        var worthSaving = !string.IsNullOrEmpty(_working.ReplacementSprite);
        if (EditorSettings.AutoSave && worthSaving && !string.IsNullOrEmpty(_working.Path))
        {
            var file = SaveCore();
            EditorWindow.SetStatus($"Auto-saved sprite rule '{_working.Path}' to {Path.GetFileName(file)}.");
            return;
        }

        DiscardCore();
        EditorWindow.SetStatus("Unsaved sprite changes discarded.", warning: worthSaving);
    }

    /// <summary>Rebuilds every widget. Only for structural changes (e.g. a new thumbnail); toggles
    /// update their own button in place.</summary>
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

        y = RenderInfo(y, width);

        if (_original != null)
            y = RenderThumbnails(y, width);

        _panel.Label("Only when sprite is", 0, y, LabelWidth, RowHeight);
        var useCurrentWidth = _original != null ? 96f : 0f;
        var originalInput = _panel.Input(_working.OriginalSprite, LabelWidth, y, width - LabelWidth - useCurrentWidth - (useCurrentWidth > 0 ? Gap : 0), RowHeight, text =>
        {
            _working.OriginalSprite = string.IsNullOrWhiteSpace(text) ? null : text.Trim();
            MarkDirty();
        }, "(any sprite on this element)");
        if (_original != null)
        {
            _panel.Button("Use current", width - useCurrentWidth, y, useCurrentWidth, RowHeight, () =>
            {
                _working.OriginalSprite = _original.name;
                MarkDirty();
                // Only that field shows it, so update it in place rather than re-rendering.
                _panel.SetInputText(originalInput, _working.OriginalSprite);
            }, UiPanel.MutedButtonColor);
        }
        y += RowStep;

        _panel.Label("Replacement PNG", 0, y, LabelWidth, RowHeight);
        _panel.Input(_working.ReplacementSprite, LabelWidth, y, width - LabelWidth - 150, RowHeight, text =>
        {
            _working.ReplacementSprite = text.Trim();
            MarkDirty();
        }, "file name without .png");
        _panel.Label(".png in sprites2/dumped/", width - 146, y, 146, RowHeight, 11, TextAnchor.MiddleLeft, UiPanel.DimTextColor);
        y += RowStep;

        _panel.Label("Name", 0, y, LabelWidth, RowHeight);
        _panel.Input(_working.Name, LabelWidth, y, width - LabelWidth - 150, RowHeight, text =>
        {
            _working.Name = string.IsNullOrWhiteSpace(text) ? null : text;
            MarkDirty();
        }, "optional description");
        var enabled = _working.IsEnabled();
        UiButton enabledButton = null;
        enabledButton = _panel.Button($"Rule enabled: {(enabled ? "On" : "Off")}", width - 146, y, 146, RowHeight, () =>
        {
            _working.Enabled = _working.IsEnabled() ? false : (bool?)null;
            MarkDirty();
            var now = _working.IsEnabled();
            enabledButton.Set($"Rule enabled: {(now ? "On" : "Off")}", now ? UiPanel.ButtonColor : UiPanel.MutedButtonColor);
        }, enabled ? UiPanel.ButtonColor : UiPanel.MutedButtonColor);
        y += RowStep + 6f;

        var quarter = (width - 3 * Gap) / 4f;
        _panel.Button("Dump original", 0, y, quarter, 26, () => Dump(overwrite: false), _image != null ? UiPanel.ButtonColor : UiPanel.MutedButtonColor);
        _panel.Button("Overwrite dump", quarter + Gap, y, quarter, 26, () => Dump(overwrite: true), UiPanel.MutedButtonColor);
        _panel.Button("Reload PNG", 2 * (quarter + Gap), y, quarter, 26, ReloadPng, UiPanel.MutedButtonColor);
        _panel.Button("Open folder", 3 * (quarter + Gap), y, quarter, 26, OpenFolder, UiPanel.MutedButtonColor);
        y += 32f;
        _panel.Button("Dump all visible sprites", 0, y, 2 * quarter + Gap, 26, DumpAll, UiPanel.MutedButtonColor);
        y += 38f;

        _panel.Button("Save", 0, y, 110, 28, Save);
        _panel.Button("Discard changes", 118, y, 140, 28, Discard, UiPanel.MutedButtonColor);
        if (_savedKey != null)
            _panel.Button("Delete rule", width - 110, y, 110, 28, Delete, UiPanel.DangerColor);
    }

    private float RenderInfo(float y, float width)
    {
        string info;
        var color = UiPanel.DimTextColor;
        var fileName = _savedKey != null ? Path.GetFileName(SpriteApplier.Repository.GetSourceFile(_savedKey)) : null;

        if (_element == null)
        {
            info = $"Not on screen - editing the rule from {fileName ?? "?"} without a live element.";
            color = UiPanel.WarningColor;
        }
        else if (_savedKey == null)
            info = "New rule - Dump original, edit the PNG, then Reload PNG. Save writes the rule to zzAdded.yaml.";
        else if (_working.Path != _element.Path)
        {
            info = $"Wildcard rule from {fileName} - it applies to every element it matches.";
            color = UiPanel.WarningColor;
        }
        else
            info = $"Rule from {fileName}.";

        if (!string.IsNullOrEmpty(_working.ReplacementSprite) && !File.Exists(SpriteApplier.PngPath(_working.ReplacementSprite)))
        {
            info += $" {_working.ReplacementSprite}.png doesn't exist yet - use Dump original.";
            color = UiPanel.WarningColor;
        }

        _panel.Label(info, 0, y, width, 34, 12, TextAnchor.UpperLeft, color);
        return y + 38f;
    }

    private float RenderThumbnails(float y, float width)
    {
        var rect = _original.rect;
        _panel.Label("Original", 0, y, ThumbnailSize, 16, 11, TextAnchor.MiddleCenter, UiPanel.DimTextColor);
        _panel.Label("Replacement", ThumbnailSize + 12, y, ThumbnailSize, 16, 11, TextAnchor.MiddleCenter, UiPanel.DimTextColor);
        y += 18f;

        var background = new Color(0.2f, 0.2f, 0.24f, 1f);
        _panel.Box(0, y, ThumbnailSize, ThumbnailSize, background);
        _panel.Picture(_original, 4, y + 4, ThumbnailSize - 8, ThumbnailSize - 8);

        var replacementX = ThumbnailSize + 12;
        _panel.Box(replacementX, y, ThumbnailSize, ThumbnailSize, background);
        var replacement = SpriteApplier.GetReplacementSprite(_working.ReplacementSprite, _original);
        if (replacement != null)
            _panel.Picture(replacement, replacementX + 4, y + 4, ThumbnailSize - 8, ThumbnailSize - 8);
        else
            _panel.Label("no PNG yet", replacementX, y, ThumbnailSize, ThumbnailSize, 12, TextAnchor.MiddleCenter, UiPanel.DimTextColor);

        var detailsX = 2 * ThumbnailSize + 24;
        var texture = _original.texture;
        var details =
            $"Sprite: {_original.name}\n" +
            $"Size: {rect.width:0} x {rect.height:0}\n" +
            $"Texture: {(texture != null ? $"{texture.name} ({texture.width} x {texture.height})" : "?")}\n" +
            $"Border: {_original.border.x:0}, {_original.border.y:0}, {_original.border.z:0}, {_original.border.w:0}";
        if (replacement != null && replacement.texture != null)
            details += $"\nPNG: {replacement.texture.width} x {replacement.texture.height}";
        _panel.Label(details, detailsX, y, width - detailsX, ThumbnailSize, 12, TextAnchor.UpperLeft, UiPanel.DimTextColor);

        return y + ThumbnailSize + 10f;
    }

    private void Dump(bool overwrite)
    {
        if (_image == null || string.IsNullOrEmpty(_working.ReplacementSprite))
        {
            EditorWindow.SetStatus(_image == null ? "Dumping needs the element on screen." : "Set a Replacement PNG name first.", warning: true);
            return;
        }

        try
        {
            var written = SpriteApplier.Dump(_image, _working.ReplacementSprite, overwrite);
            EditorWindow.SetStatus(written
                ? $"Dumped to sprites2/dumped/{_working.ReplacementSprite}.png - edit it, then Reload PNG."
                : $"{_working.ReplacementSprite}.png already exists - use Overwrite dump to replace it.", warning: !written);

            if (written)
                SpriteApplier.ReloadFile(_working.ReplacementSprite);

            // A new rule starts once there's an image for it, so leaving auto-saves it.
            if (_savedKey == null)
                MarkDirty();
            Render();
        }
        catch (Exception ex)
        {
            EditorWindow.SetStatus($"Dump failed: {ex.Message}", warning: true);
        }
    }

    private void DumpAll()
    {
        try
        {
            var count = SpriteApplier.DumpAllVisible();
            EditorWindow.SetStatus($"Dumped {count} new sprite(s) to sprites2/dumped/ (existing files kept).");
        }
        catch (Exception ex)
        {
            EditorWindow.SetStatus($"Dump failed: {ex.Message}", warning: true);
        }
    }

    private void ReloadPng()
    {
        if (string.IsNullOrEmpty(_working.ReplacementSprite))
            return;

        SpriteApplier.ReloadFile(_working.ReplacementSprite);
        EditorWindow.SetStatus($"Reloaded {_working.ReplacementSprite}.png.");
        Render();
    }

    private static void OpenFolder()
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = SpriteApplier.DumpFolder, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            EditorWindow.SetStatus($"Could not open {SpriteApplier.DumpFolder}: {ex.Message}", warning: true);
        }
    }

    private void MarkDirty() => _dirty = true;

    private void Preview()
    {
        var repository = SpriteApplier.Repository;
        var key = SpriteContract.KeyOf(_working);
        var patterns = new List<string> { _working.Path, PathOf(_previewKey) };

        if (_previewKey != key)
            repository.DiscardPreview(_previewKey);

        if (!string.IsNullOrEmpty(_working.Path))
            repository.Preview(_working with { });

        _previewKey = key;
        _hasPreview = true;
        _dirty = false;
        _lastPreviewTime = Time.realtimeSinceStartup;

        SpriteApplier.Refresh(patterns);
        EditorWindow.SetStatus("Previewing - Save to keep this rule.");
    }

    private void Save()
    {
        if (string.IsNullOrEmpty(_working.Path) || string.IsNullOrEmpty(_working.ReplacementSprite))
        {
            EditorWindow.SetStatus("The rule needs a path and a Replacement PNG name.", warning: true);
            return;
        }

        var savedBefore = _savedKey;
        var file = SaveCore();
        EditorWindow.SetStatus($"Saved to {Path.GetFileName(file)}.");
        // The info line and Delete button depend on which rule is saved; nothing else changes.
        if (savedBefore != _savedKey)
            Render();
    }

    private string SaveCore()
    {
        var repository = SpriteApplier.Repository;
        var key = SpriteContract.KeyOf(_working);
        var patterns = new List<string> { _working.Path, PathOf(_savedKey), PathOf(_previewKey) };
        if (_previewKey != key)
            repository.DiscardPreview(_previewKey);

        var file = repository.Save(_working with { }, _savedKey);
        _savedKey = key;
        _previewKey = key;
        _dirty = false;
        _hasPreview = false;

        SpriteApplier.Refresh(patterns);
        return file;
    }

    private void Discard()
    {
        DiscardCore();
        Build(_panel, _element, _element == null ? _savedKey : null);
        EditorWindow.SetStatus("Reloaded the sprite rule from disk.");
    }

    private void DiscardCore()
    {
        var patterns = new List<string> { _working.Path, PathOf(_savedKey), PathOf(_previewKey) };
        SpriteApplier.Repository.Load();
        SpriteApplier.Refresh(patterns);
        _dirty = false;
        _hasPreview = false;
    }

    private void Delete()
    {
        var repository = SpriteApplier.Repository;
        var patterns = new List<string> { _working.Path, PathOf(_savedKey), PathOf(_previewKey) };
        var deleted = _savedKey;
        repository.Delete(_savedKey);
        repository.DiscardPreview(_previewKey);
        SpriteApplier.Refresh(patterns);
        EditorWindow.SetStatus($"Deleted sprite rule '{deleted}' (the PNG is kept).");
        Build(_panel, _element, _element == null ? deleted : null);
    }

    // Includes disabled rules, so they can be found and re-enabled.
    private static SpriteContract FindRuleFor(string path, string spriteName)
    {
        foreach (var contract in SpriteApplier.Repository.FindCandidates(path))
        {
            if (string.IsNullOrEmpty(contract.OriginalSprite) || contract.OriginalSprite == spriteName)
                return contract;
        }
        return null;
    }

    // Keys are "path" or "path [sprite]"; patterns only need the path part.
    private static string PathOf(string key)
    {
        if (key == null)
            return null;
        var bracket = key.LastIndexOf(" [", StringComparison.Ordinal);
        return bracket > 0 && key.EndsWith("]") ? key.Substring(0, bracket) : key;
    }
}
