using System.Collections.Generic;
using FanslationStudio.Plugins.UnityShared.Editor.Ui;

namespace FanslationStudio.Plugins.UnityShared.Editor.Tabs;

/// <summary>A saved rule, as listed in the window's Rules mode.</summary>
internal sealed class RuleSummary
{
    /// <summary>Unique id within the tab (usually the path; sprite rules add the sprite name).</summary>
    public string Key;
    public string Path;
    public string Description;
    public string File;
}

/// <summary>One tab of the editor window (Layout, Text, Sprite).</summary>
internal interface IEditorTab
{
    string Title { get; }

    /// <summary>Whether this tab applies to the element (e.g. Text needs a text component).
    /// <paramref name="element"/> is null when editing a rule that isn't on screen.</summary>
    bool IsAvailable(PickedElement element);

    /// <summary>
    /// Builds the tab's widgets into an empty panel. Edits the rule with key <paramref name="ruleKey"/>
    /// if given, otherwise the rule that applies to the element. <paramref name="element"/> is
    /// null when the rule has no matching element on screen.
    /// </summary>
    void Build(UiPanel panel, PickedElement element, string ruleKey);

    /// <summary>Called every frame while the tab is shown (e.g. for throttled previews).</summary>
    void Tick();

    /// <summary>Called before the tab is torn down (selection change, tab switch, window close),
    /// so unsaved changes can be auto-saved or discarded.</summary>
    void Leave();

    /// <summary>Every saved rule of this tab's kind, in file order.</summary>
    IReadOnlyList<RuleSummary> ListRules();

    /// <summary>Key of the saved rule currently open, if any (highlighted in the Rules list).</summary>
    string EditingRulePath { get; }

    /// <summary>Changes whenever this tab's rules change, so the Rules list can refresh.</summary>
    int RulesVersion { get; }
}
