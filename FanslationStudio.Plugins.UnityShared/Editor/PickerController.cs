using System;
using System.Collections.Generic;
using BepInEx;
using FanslationStudio.Plugins.Shared;
using UnityEngine;

namespace FanslationStudio.Plugins.UnityShared.Editor;

/// <summary>
/// Owns the current pick: the stack of elements under the cursor, which one is selected, and
/// navigation (cycle through the stack, walk up/down the hierarchy). Call <see cref="Tick"/> once
/// per frame from the host.
/// </summary>
internal static class PickerController
{
    private static IPluginLogger _logger;
    private static UiEditorHotkeys _hotkeys = new UiEditorHotkeys();

    private static List<PickedElement> _stack = new List<PickedElement>();
    private static int _stackIndex = -1;
    private static PickedElement _selected;
    private static bool _logOverlayState;

    // Elements we walked up from with Parent, so Child can walk back down the same way.
    private static readonly Stack<RectTransform> _descentTrail = new Stack<RectTransform>();

    /// <summary>Raised when the selection changes (null when cleared).</summary>
    public static event Action<PickedElement> SelectionChanged;

    /// <summary>Raised after the pick hotkey collects a new stack.</summary>
    public static event Action Picked;

    public static UiEditorHotkeys Hotkeys => _hotkeys;

    public static PickedElement Selected => _selected != null && _selected.IsAlive ? _selected : null;
    public static IReadOnlyList<PickedElement> Stack => _stack;
    public static int StackIndex => _stackIndex;

    public static void Configure(IPluginLogger logger, UiEditorHotkeys hotkeys)
    {
        _logger = logger;
        _hotkeys = hotkeys ?? new UiEditorHotkeys();
    }

    public static void Tick()
    {
        var input = UnityInput.Current;

        // Check Clear before Pick: they share a main key and differ only by modifier.
        if (_hotkeys.Clear.IsDown())
            Clear();
        else if (_hotkeys.Pick.IsDown())
            PickAt(input.mousePosition);

        // Navigation keys ([ ] PageUp/PageDown) are ordinary typing while a field has focus.
        if ((_stack.Count > 0 || _selected != null) && !EditorWindow.IsTyping)
        {
            var scroll = IsWheelModifierHeld(input) ? GetScroll(input) : 0f;

            if (_hotkeys.Next.IsDown() || scroll < 0f)
                Cycle(1);
            else if (_hotkeys.Previous.IsDown() || scroll > 0f)
                Cycle(-1);
            else if (_hotkeys.Parent.IsDown())
                SelectParent();
            else if (_hotkeys.Child.IsDown())
                SelectChild();
        }

        if (_selected != null && !_selected.IsAlive)
            Clear();

        RefreshOverlay();

        if (_logOverlayState)
        {
            _logOverlayState = false;
            _logger?.LogDebug($"[UIEditor] Selected {Selected?.Path ?? "(none)"} - {HighlightOverlay.Describe()}");
        }
    }

    private static string CycleText()
    {
        var keys = $"{UiEditorHotkeys.Describe(_hotkeys.Previous)} / {UiEditorHotkeys.Describe(_hotkeys.Next)}";
        return _hotkeys.WheelText != null ? $"{_hotkeys.WheelText} or {keys}" : keys;
    }

    private static bool IsWheelModifierHeld(IInputSystem input)
    {
        var modifier = _hotkeys.WheelModifier;
        if (modifier == KeyCode.None)
            return false;
        return input.GetKey(modifier) || input.GetKey(OtherSide(modifier));
    }

    private static KeyCode OtherSide(KeyCode key)
    {
        switch (key)
        {
            case KeyCode.LeftShift: return KeyCode.RightShift;
            case KeyCode.RightShift: return KeyCode.LeftShift;
            case KeyCode.LeftControl: return KeyCode.RightControl;
            case KeyCode.RightControl: return KeyCode.LeftControl;
            case KeyCode.LeftAlt: return KeyCode.RightAlt;
            case KeyCode.RightAlt: return KeyCode.LeftAlt;
            default: return key;
        }
    }

    // Some platforms report Shift+wheel as horizontal scrolling, so accept either axis.
    private static float GetScroll(IInputSystem input)
    {
        var delta = input.mouseScrollDelta;
        return Mathf.Abs(delta.y) >= Mathf.Abs(delta.x) ? delta.y : delta.x;
    }

    public static void PickAt(Vector2 screenPoint)
    {
        _stack = ElementPicker.PickAt(screenPoint);
        _descentTrail.Clear();

        _logger?.LogInfo($"[UIEditor] Picked {_stack.Count} element(s) at {screenPoint}:");
        for (var i = 0; i < _stack.Count; i++)
            _logger?.LogInfo($"[UIEditor]   {i + 1}. {_stack[i].Path} {_stack[i].CapabilityTags}");

        if (_stack.Count == 0)
        {
            Clear();
            return;
        }

        SelectStackIndex(0);
        Picked?.Invoke();
    }

    public static void Cycle(int direction)
    {
        if (_stack.Count == 0)
            return;

        var index = _stackIndex < 0 ? 0 : (_stackIndex + direction + _stack.Count) % _stack.Count;
        _descentTrail.Clear();
        SelectStackIndex(index);
    }

    public static void SelectStackIndex(int index)
    {
        if (index < 0 || index >= _stack.Count)
            return;

        _stackIndex = index;
        SetSelected(_stack[index]);
    }

    public static void SelectParent()
    {
        var current = Selected?.RectTransform;
        var parent = current == null ? null : UiCompat.As<RectTransform>(current.parent);
        if (parent == null || UiCompat.GetComponentInParent<Canvas>(parent) == null)
            return;

        _descentTrail.Push(current);
        SelectElement(parent);
    }

    public static void SelectChild()
    {
        var current = Selected?.RectTransform;
        if (current == null)
            return;

        RectTransform child = null;
        if (_descentTrail.Count > 0 && _descentTrail.Peek() != null && _descentTrail.Peek().parent == current)
            child = _descentTrail.Pop();

        if (child == null)
        {
            _descentTrail.Clear();
            for (var i = 0; i < current.childCount && child == null; i++)
            {
                var candidate = UiCompat.As<RectTransform>(current.GetChild(i));
                if (candidate != null && candidate.gameObject.activeInHierarchy)
                    child = candidate;
            }
        }

        if (child != null)
            SelectElement(child);
    }

    public static void Clear()
    {
        _stack = new List<PickedElement>();
        _stackIndex = -1;
        _descentTrail.Clear();
        SetSelected(null);
    }

    /// <summary>Selects any element, e.g. one found from a rule. Keeps the stack index in sync if it is in the stack.</summary>
    public static void SelectElement(RectTransform rectTransform)
    {
        // Keep the stack position in sync if we navigated onto an element that's in it.
        _stackIndex = _stack.FindIndex(e => e.RectTransform == rectTransform);
        SetSelected(_stackIndex >= 0 ? _stack[_stackIndex] : PickedElement.From(rectTransform));
    }

    private static void SetSelected(PickedElement element)
    {
        _selected = element;
        _logOverlayState = true;
        SelectionChanged?.Invoke(element);
    }

    private static void RefreshOverlay()
    {
        var selected = Selected;
        if (selected == null)
        {
            HighlightOverlay.Hide();
            return;
        }

        var position = _stackIndex >= 0 ? $"{_stackIndex + 1}/{_stack.Count}" : "–";
        HighlightOverlay.Show(selected.RectTransform,
            $"[{position}] {selected}   {PickedElement.Truncate(selected.Path, 90)}   " +
            $"({CycleText()}: cycle, " +
            $"{UiEditorHotkeys.Describe(_hotkeys.Parent)} / {UiEditorHotkeys.Describe(_hotkeys.Child)}: parent/child, " +
            $"{UiEditorHotkeys.Describe(_hotkeys.Clear)}: clear)");
    }
}
