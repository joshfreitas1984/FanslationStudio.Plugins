using FanslationStudio.Plugins.UnityShared.Editor.Ui;

namespace FanslationStudio.Plugins.UnityShared.Editor.Tabs;

/// <summary>One tab of the editor window (Layout, Text, Sprite).</summary>
internal interface IEditorTab
{
    string Title { get; }

    /// <summary>Whether this tab applies to the element (e.g. Text needs a text component).</summary>
    bool IsAvailable(PickedElement element);

    /// <summary>Builds the tab's widgets for the element into an empty panel.</summary>
    void Build(UiPanel panel, PickedElement element);

    /// <summary>Called every frame while the tab is shown (e.g. for throttled previews).</summary>
    void Tick();

    /// <summary>Called before the tab is torn down (selection change, tab switch, window close),
    /// so unsaved previews can be discarded.</summary>
    void Leave();
}
