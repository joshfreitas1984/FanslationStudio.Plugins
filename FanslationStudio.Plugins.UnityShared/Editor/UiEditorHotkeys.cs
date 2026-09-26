using UnityEngine;
#if IL2CPP
using BepInEx.Unity.IL2CPP.Configuration;
#elif BEPINEX6_MONO
using BepInEx.Unity.Mono.Configuration;
#else
using BepInEx.Configuration;
#endif

namespace FanslationStudio.Plugins.UnityShared.Editor;

internal sealed class UiEditorHotkeys
{
    public KeyboardShortcut Pick = new KeyboardShortcut(KeyCode.F8);
    public KeyboardShortcut Clear = new KeyboardShortcut(KeyCode.F8, KeyCode.LeftShift);
    public KeyboardShortcut Next = new KeyboardShortcut(KeyCode.PageDown);
    public KeyboardShortcut Previous = new KeyboardShortcut(KeyCode.PageUp);
    public KeyboardShortcut Parent = new KeyboardShortcut(KeyCode.LeftBracket);
    public KeyboardShortcut Child = new KeyboardShortcut(KeyCode.RightBracket);
    public KeyboardShortcut Reload = new KeyboardShortcut(KeyCode.F8, KeyCode.LeftControl);

    public static UiEditorHotkeys Bind(BepInEx.Configuration.ConfigFile config)
    {
        var defaults = new UiEditorHotkeys();
        return new UiEditorHotkeys
        {
            Pick = config.Bind("Hotkeys", "Pick", defaults.Pick,
                "Collects every UI element under the cursor and selects the topmost one").Value,
            Clear = config.Bind("Hotkeys", "ClearSelection", defaults.Clear,
                "Clears the current selection and hides the highlight").Value,
            Next = config.Bind("Hotkeys", "NextElement", defaults.Next,
                "Selects the next (lower) element under the cursor. Ctrl+Mouse wheel also works").Value,
            Previous = config.Bind("Hotkeys", "PreviousElement", defaults.Previous,
                "Selects the previous (higher) element under the cursor").Value,
            Parent = config.Bind("Hotkeys", "SelectParent", defaults.Parent,
                "Selects the parent of the current element").Value,
            Child = config.Bind("Hotkeys", "SelectChild", defaults.Child,
                "Selects a child of the current element (walks back down after SelectParent)").Value,
            Reload = config.Bind("Hotkeys", "ReloadAll", defaults.Reload,
                "Reloads layout files from disk and re-applies them").Value,
        };
    }
}
