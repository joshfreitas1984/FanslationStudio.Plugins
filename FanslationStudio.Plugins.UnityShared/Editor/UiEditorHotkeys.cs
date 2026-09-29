using System.Collections.Generic;
using System.Linq;
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
    // Unity numbers mouse buttons from 0: Mouse2 = middle, Mouse3 = back, Mouse4 = forward.
    public KeyboardShortcut Pick = new KeyboardShortcut(KeyCode.Mouse2, KeyCode.LeftAlt);
    public KeyboardShortcut PickAppeared = new KeyboardShortcut(KeyCode.Alpha2, KeyCode.LeftAlt);
    public KeyboardShortcut PickAppearedShift = new KeyboardShortcut(KeyCode.Alpha2, KeyCode.LeftShift, KeyCode.LeftAlt);
    public KeyboardShortcut Clear = new KeyboardShortcut(KeyCode.Mouse3, KeyCode.LeftAlt);
    public KeyboardShortcut Next = new KeyboardShortcut(KeyCode.PageDown);
    public KeyboardShortcut Previous = new KeyboardShortcut(KeyCode.PageUp);
    public KeyboardShortcut Parent = new KeyboardShortcut(KeyCode.LeftBracket);
    public KeyboardShortcut Child = new KeyboardShortcut(KeyCode.RightBracket);
    public KeyboardShortcut Reload = new KeyboardShortcut(KeyCode.Mouse4, KeyCode.LeftAlt);
    public KeyboardShortcut ToggleWindow = new KeyboardShortcut(KeyCode.Alpha1, KeyCode.LeftAlt);
    /// <summary>Hold this and scroll to cycle through the elements under the cursor. None disables.</summary>
    public KeyCode WheelModifier = KeyCode.LeftAlt;

    /// <summary>Wheel cycling as shown to the user, e.g. "Shift + Wheel".</summary>
    public string WheelText => WheelModifier == KeyCode.None ? null : $"{Describe(WheelModifier)} + Wheel";

    /// <summary>A hotkey as shown to the user: modifiers first, friendly names ("Shift + Mouse Forward").</summary>
    public static string Describe(KeyboardShortcut shortcut)
    {
        if (shortcut.MainKey == KeyCode.None)
            return "(unbound)";

        var parts = new List<string>(shortcut.Modifiers.Select(Describe)) { Describe(shortcut.MainKey) };
        return string.Join(" + ", parts.ToArray());
    }

    public static string Describe(KeyCode key)
    {
        switch (key)
        {
            case KeyCode.LeftShift: case KeyCode.RightShift: return "Shift";
            case KeyCode.LeftControl: case KeyCode.RightControl: return "Ctrl";
            case KeyCode.LeftAlt: case KeyCode.RightAlt: return "Alt";
            case KeyCode.Mouse0: return "Left Click";
            case KeyCode.Mouse1: return "Right Click";
            case KeyCode.Mouse2: return "Middle Click";
            case KeyCode.Mouse3: return "Mouse Back";
            case KeyCode.Mouse4: return "Mouse Forward";
            case KeyCode.PageUp: return "Page Up";
            case KeyCode.PageDown: return "Page Down";
            case KeyCode.LeftBracket: return "[";
            case KeyCode.RightBracket: return "]";
            case KeyCode.BackQuote: return "~";
            default:
                // Alpha0..Alpha9 are the number row.
                if (key >= KeyCode.Alpha0 && key <= KeyCode.Alpha9)
                    return ((int)(key - KeyCode.Alpha0)).ToString();
                return key.ToString();
        }
    }

    public static UiEditorHotkeys Bind(BepInEx.Configuration.ConfigFile config)
    {
        var defaults = new UiEditorHotkeys();
        return new UiEditorHotkeys
        {
            Pick = config.Bind("Hotkeys", "Pick", defaults.Pick,
                "Collects every UI element under the cursor, selects the topmost one and opens the editor. Mouse3 = back side button, Mouse4 = forward, Mouse2 = middle").Value,
            PickAppeared = config.Bind("Hotkeys", "PickAppeared", defaults.PickAppeared,
                "For tooltips and popups: press once to remember what's on screen, hover to open the tooltip, then " +
                "press again to pick whatever appeared or changed (or what's under the cursor if nothing did)").Value,
            PickAppearedShift = config.Bind("Hotkeys", "PickAppearedShift", defaults.PickAppearedShift,
                "Same as PickAppeared, for games that only show popups while Shift is held").Value,
            Clear = config.Bind("Hotkeys", "ClearSelection", defaults.Clear,
                "Clears the current selection and hides the highlight. Also cancels a PickAppeared in progress").Value,
            Next = config.Bind("Hotkeys", "NextElement", defaults.Next,
                "Selects the next (lower) element under the cursor. WheelModifier + mouse wheel also works").Value,
            Previous = config.Bind("Hotkeys", "PreviousElement", defaults.Previous,
                "Selects the previous (higher) element under the cursor").Value,
            Parent = config.Bind("Hotkeys", "SelectParent", defaults.Parent,
                "Selects the parent of the current element").Value,
            Child = config.Bind("Hotkeys", "SelectChild", defaults.Child,
                "Selects a child of the current element (walks back down after SelectParent)").Value,
            Reload = config.Bind("Hotkeys", "ReloadAll", defaults.Reload,
                "Reloads layout, sprite and resizer files from disk and re-applies them").Value,
            ToggleWindow = config.Bind("Hotkeys", "ToggleWindow", defaults.ToggleWindow,
                "Shows or hides the editor window").Value,
            WheelModifier = config.Bind("Hotkeys", "WheelModifier", defaults.WheelModifier,
                "Hold this key and scroll the mouse wheel to cycle through the elements under the cursor. " +
                "Either side's key works (LeftAlt also accepts RightAlt). None disables wheel cycling").Value,
        };
    }
}
