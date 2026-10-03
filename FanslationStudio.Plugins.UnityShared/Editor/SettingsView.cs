using System;
using System.Collections.Generic;
using System.IO;
using BepInEx.Configuration;
using FanslationStudio.Plugins.UnityShared.Editor.Ui;
using UnityEngine;
#if IL2CPP
using BepInEx.Unity.IL2CPP.Configuration;
#elif BEPINEX6_MONO
using BepInEx.Unity.Mono.Configuration;
#endif

namespace FanslationStudio.Plugins.UnityShared.Editor;

/// <summary>
/// The window's Plugin settings view: the Dump strings button plus every entry in the shared
/// <see cref="PluginConfig"/> file, grouped by section and paged. Built generically from the
/// config file's entries, so a plugin's new settings show up here without touching this class.
/// </summary>
internal static class SettingsView
{
    private const float HeaderHeight = 58f;
    private const float FooterHeight = 30f;
    private const float SectionHeight = 26f;
    private const float EntryHeight = 40f;
    private const float KeyWidth = 190f;
    private const float ControlWidth = 250f;

    private static int _page;

    // ConfigFile.SaveOnConfigSet rewrites the whole .cfg on every set, i.e. on every valid
    // keystroke here. While the view is open, changes apply in memory (SettingChanged still
    // fires) and the file is written once typing pauses, and again when the view closes.
    private const float SaveDelaySeconds = 1f;
    private static bool _editing;
    private static bool _saveOnConfigSet;
    private static bool _changed;
    private static float _lastChangeTime;

    /// <summary>Call when the view opens; pair with <see cref="EndEditing"/>.</summary>
    public static void BeginEditing()
    {
        if (_editing)
            return;

        var config = PluginConfig.File;
        _editing = true;
        _changed = false;
        _saveOnConfigSet = config.SaveOnConfigSet;
        config.SaveOnConfigSet = false;
    }

    /// <summary>Call when the view closes: writes the file if anything changed.</summary>
    public static void EndEditing()
    {
        if (!_editing)
            return;

        var config = PluginConfig.File;
        _editing = false;
        config.SaveOnConfigSet = _saveOnConfigSet;
        if (!_changed)
            return;

        SaveNow();
    }

    /// <summary>Call every frame while the view is open: saves a second after the last change,
    /// so edits survive the game being closed with the view still open.</summary>
    public static void Tick()
    {
        if (_editing && _changed && Time.realtimeSinceStartup - _lastChangeTime >= SaveDelaySeconds)
            SaveNow();
    }

    private static void SaveNow()
    {
        var config = PluginConfig.File;
        _changed = false;
        try
        {
            config.Save();
            EditorWindow.SetStatus($"Saved settings to {Path.GetFileName(config.ConfigFilePath)}.");
        }
        catch (Exception ex)
        {
            EditorWindow.SetStatus($"Could not save settings: {ex.Message}", true);
        }
    }

    /// <summary>Builds the view into an empty panel.</summary>
    public static void Build(UiPanel panel, Action close)
    {
        var config = PluginConfig.File;
        var width = panel.Width;

        panel.Label("Plugin settings", 0, 0, 300, 24, 14, TextAnchor.MiddleLeft, null, FontStyle.Bold);
        panel.Button("Dump strings", width - 214, 0, 120, 24, UiEditorHost.DumpStrings, UiPanel.ButtonColor);
        panel.Button("Close", width - 88, 0, 88, 24, close, UiPanel.MutedButtonColor);
        panel.Label($"Saved to {Path.GetFileName(config.ConfigFilePath)} as you edit. " +
                    "Most changes take effect after restarting the game.",
            0, 28, width, 22, 12, TextAnchor.MiddleLeft, UiPanel.DimTextColor);

        var pages = Paginate(config, panel.Rect.rect.height - HeaderHeight - FooterHeight);
        _page = Mathf.Clamp(_page, 0, pages.Count - 1);

        var y = HeaderHeight;
        string section = null;
        foreach (var entry in pages[_page])
        {
            var definition = entry.Definition;
            if (definition.Section != section)
            {
                section = definition.Section;
                panel.Box(0, y + SectionHeight - 3, width, 1, UiPanel.MutedButtonColor);
                panel.Label(section, 0, y, width, SectionHeight - 4, 13, TextAnchor.MiddleLeft, null, FontStyle.Bold);
                y += SectionHeight;
            }

            BuildEntry(panel, entry, y, width, close);
            y += EntryHeight;
        }

        var footerY = panel.Rect.rect.height - FooterHeight + 4f;
        panel.Button("<", 0, footerY, 36, 24, () => { _page--; Rebuild(panel, close); }, UiPanel.MutedButtonColor);
        panel.Label($"{_page + 1}/{pages.Count}", 40, footerY, width - 80, 24, 12, TextAnchor.MiddleCenter, UiPanel.DimTextColor);
        panel.Button(">", width - 36, footerY, 36, 24, () => { _page++; Rebuild(panel, close); }, UiPanel.MutedButtonColor);
    }

    private static void Rebuild(UiPanel panel, Action close)
    {
        panel.Clear();
        Build(panel, close);
    }

    private static void BuildEntry(UiPanel panel, ConfigEntryBase entry, float y, float width, Action close)
    {
        var key = entry.Definition.Key;
        panel.Label(key, 12, y, KeyWidth - 16, EntryHeight - 6, 13);

        var controlX = KeyWidth;
        if (entry.SettingType == typeof(bool))
        {
            var on = (bool)entry.BoxedValue;
            UiButton button = null;
            button = panel.Button(on ? "On" : "Off", controlX, y + 4, 80, 24, () =>
            {
                var value = !(bool)entry.BoxedValue;
                entry.BoxedValue = value;
                Saved(entry);
                button.Set(value ? "On" : "Off", value ? UiPanel.SelectedColor : UiPanel.MutedButtonColor);
            }, on ? UiPanel.SelectedColor : UiPanel.MutedButtonColor);
        }
        else
        {
            panel.Input(ToText(entry), controlX, y + 4, ControlWidth, 24, text => Apply(entry, text));
        }

        var descriptionX = controlX + ControlWidth + 12f;
        panel.Label(entry.Description?.Description, descriptionX, y, width - descriptionX, EntryHeight - 4, 11,
            TextAnchor.MiddleLeft, UiPanel.DimTextColor);
    }

    // Strings are edited as-is; everything else in the same form as the .cfg file.
    private static string ToText(ConfigEntryBase entry) =>
        entry.SettingType == typeof(string) ? (string)entry.BoxedValue : entry.GetSerializedValue();

    private static void Apply(ConfigEntryBase entry, string text)
    {
        object value = null;
        try
        {
            // KeyboardShortcut's own parser logs an error and returns "unbound" rather than
            // throwing, so check it here instead of logging on every keystroke.
            if (entry.SettingType != typeof(KeyboardShortcut) || IsValidShortcut(text))
                value = entry.SettingType == typeof(string) ? text : TomlTypeConverter.ConvertToValue(text, entry.SettingType);
        }
        catch (Exception)
        {
            value = null;
        }

        if (value == null)
        {
            // Usually a half-typed value; keep the old one until it parses.
            EditorWindow.SetStatus($"{entry.Definition.Key}: \"{text}\" isn't a valid {entry.SettingType.Name}.", true);
            return;
        }

        var acceptable = entry.Description?.AcceptableValues;
        if (acceptable != null && !acceptable.IsValid(value))
        {
            EditorWindow.SetStatus($"{entry.Definition.Key}: {acceptable.ToDescriptionString()}", true);
            return;
        }

        entry.BoxedValue = value;
        Saved(entry);
    }

    // Same format KeyboardShortcut.Deserialize accepts, e.g. "Mouse2 + LeftAlt".
    private static bool IsValidShortcut(string text)
    {
        var parts = text.Split(new[] { ' ', '+', ',', ';', '|' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return false;
        foreach (var part in parts)
        {
            if (!Enum.IsDefined(typeof(KeyCode), part))
                return false;
        }
        return true;
    }

    private static void Saved(ConfigEntryBase entry)
    {
        // Outside BeginEditing/EndEditing SaveOnConfigSet is untouched, so the set already saved.
        if (_editing)
        {
            _changed = true;
            _lastChangeTime = Time.realtimeSinceStartup;
        }
        EditorWindow.SetStatus($"Set {entry.Definition.Section} / {entry.Definition.Key}.");
    }

    /// <summary>Splits the entries, grouped by section in the order they were bound, into pages
    /// that fit the given height (a section header is repeated when it spans pages).</summary>
    private static List<List<ConfigEntryBase>> Paginate(ConfigFile config, float height)
    {
        var sections = new List<string>();
        var bySection = new Dictionary<string, List<ConfigEntryBase>>();
        foreach (var pair in config)
        {
            var section = pair.Key.Section;
            if (!bySection.TryGetValue(section, out var entries))
            {
                sections.Add(section);
                bySection[section] = entries = new List<ConfigEntryBase>();
            }
            entries.Add(pair.Value);
        }

        var pages = new List<List<ConfigEntryBase>> { new List<ConfigEntryBase>() };
        var used = 0f;
        foreach (var section in sections)
        {
            var needsHeader = true;
            foreach (var entry in bySection[section])
            {
                var needed = EntryHeight + (needsHeader ? SectionHeight : 0f);
                if (used + needed > height && pages[pages.Count - 1].Count > 0)
                {
                    pages.Add(new List<ConfigEntryBase>());
                    used = 0f;
                    needed = EntryHeight + SectionHeight;
                }

                pages[pages.Count - 1].Add(entry);
                used += needed;
                needsHeader = false;
            }
        }
        return pages;
    }
}
