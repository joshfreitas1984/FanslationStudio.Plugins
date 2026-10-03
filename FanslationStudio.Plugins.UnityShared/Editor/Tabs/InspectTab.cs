using System;
using System.Collections.Generic;
using System.Text;
using FanslationStudio.Plugins.Support;
using FanslationStudio.Plugins.UnityShared.Editor.Ui;
using UnityEngine;
using UnityEngine.UI;

namespace FanslationStudio.Plugins.UnityShared.Editor.Tabs;

/// <summary>
/// Read-only browser for every component on the selected element, plus its descendants (walked
/// with "Up"/child buttons, since the picker's own stack only covers ancestors of what's under the
/// cursor). Shows field values for the component types most relevant to diagnosing layout/sizing
/// issues, an "Edit as rule" button that jumps the main picker/Layout tab to whichever node is
/// being browsed, and a Copy-able field dump. All editing of layout rule fields (including
/// ContentSizeFitter/LayoutGroup) happens on the Layout tab itself - this tab never writes rules,
/// so there's exactly one save/reload path for a rule's fields, not two racing each other.
/// Aimed at diagnosing "what's actually controlling this element's size/position" without needing
/// the Unity Editor, which isn't available against a compiled game.
/// </summary>
internal sealed class InspectTab : IEditorTab
{
    private const float ButtonHeight = 24f;
    private const float ButtonWidth = 150f;
    private const float Gap = 4f;
    private const float RowHeight = 20f;
    private const int MaxChildButtons = 50;

    private UiPanel _panel;
    private RectTransform _root;
    private Component[] _components = Array.Empty<Component>();
    private int _selectedIndex;

    public string Title => "Inspect";

    public bool IsAvailable(PickedElement element) => element != null;

    public string EditingRulePath => null;

    public int RulesVersion => 0;

    public IReadOnlyList<RuleSummary> ListRules() => Array.Empty<RuleSummary>();

    public void Build(UiPanel panel, PickedElement element, string ruleKey)
    {
        _panel = panel;
        _root = element != null && element.IsAlive ? element.RectTransform : null;
        _selectedIndex = 0;
        RefreshComponents();
        Render();
    }

    public void Tick()
    {
    }

    public void Leave()
    {
    }

    private void RefreshComponents()
    {
        _components = _root != null ? UiCompat.GetComponents(_root.gameObject) : Array.Empty<Component>();
        if (_selectedIndex >= _components.Length)
            _selectedIndex = 0;
    }

    private void Navigate(RectTransform target)
    {
        if (target == null)
            return;
        _root = target;
        _selectedIndex = 0;
        RefreshComponents();
        Render();
    }

    private void Render()
    {
        _panel.Clear();
        var width = _panel.Width;

        if (_root == null)
        {
            _panel.Label("No element selected.", 0, 0, width, 24, 12, TextAnchor.UpperLeft, UiPanel.DimTextColor);
            return;
        }

        var y = 0f;

        // ---- Breadcrumb: current path, an Up button to walk toward the root, and a way to jump
        // straight to editing THIS node's rule (Text/Sprite/Layout) instead of only the top-level
        // element the picker originally selected. -------------------------------------------------
        var parent = UiCompat.As<RectTransform>(_root.parent);
        _panel.Button("< Up", 0, y, 70, ButtonHeight, parent != null ? () => Navigate(parent) : (Action)null,
            parent != null ? UiPanel.MutedButtonColor : new Color(0.18f, 0.18f, 0.2f, 1f));
        var root = _root;
        _panel.Button("Edit as rule >", 78, y, 140, ButtonHeight, () => EditorWindow.SelectForEditing(root), UiPanel.ButtonColor);
        _panel.Label(ObjectHelper.GetGameObjectPath(_root.gameObject), 226, y, width - 226, ButtonHeight, 11,
            TextAnchor.MiddleLeft, UiPanel.DimTextColor);
        y += ButtonHeight + 10f;

        // ---- Children: this element's own children, to drill further down the tree. ----------
        var childCount = _root.childCount;
        if (childCount > 0)
        {
            _panel.Label($"Children ({childCount}):", 0, y, width, 16, 11, TextAnchor.MiddleLeft, UiPanel.DimTextColor);
            y += 18f;

            // Some containers (scroll lists, grids) have hundreds of children; a button each would
            // be hundreds of objects per render, mostly off the bottom of the panel anyway.
            var shown = Mathf.Min(childCount, MaxChildButtons);
            var columns = Mathf.Max(1, (int)((width + Gap) / (ButtonWidth + Gap)));
            for (var i = 0; i < shown; i++)
            {
                var child = UiCompat.As<RectTransform>(_root.GetChild(i));
                if (child == null)
                    continue;

                var col = i % columns;
                var row = i / columns;
                _panel.Button(child.name, col * (ButtonWidth + Gap), y + row * (ButtonHeight + Gap), ButtonWidth, ButtonHeight,
                    () => Navigate(child), UiPanel.MutedButtonColor);
            }
            y += (float)Math.Ceiling(shown / (double)columns) * (ButtonHeight + Gap) + 8f;

            if (childCount > shown)
            {
                _panel.Label($"+{childCount - shown} more children not shown (pick one on screen, or pick a deeper element and use Up).",
                    0, y - 4f, width, 16, 11, TextAnchor.MiddleLeft, UiPanel.DimTextColor);
                y += 16f;
            }
        }

        // ---- Components on this element. ------------------------------------------------------
        if (_components.Length == 0)
        {
            _panel.Label("No components on this element.", 0, y, width, 24, 12, TextAnchor.UpperLeft, UiPanel.DimTextColor);
            return;
        }

        _panel.Label("Components:", 0, y, width, 16, 11, TextAnchor.MiddleLeft, UiPanel.DimTextColor);
        y += 18f;

        var componentColumns = Mathf.Max(1, (int)((width + Gap) / (ButtonWidth + Gap)));
        for (var i = 0; i < _components.Length; i++)
        {
            var col = i % componentColumns;
            var row = i / componentColumns;
            var index = i;
            var name = PickedElement.ComponentTypeName(_components[i]);
            _panel.Button(name, col * (ButtonWidth + Gap), y + row * (ButtonHeight + Gap), ButtonWidth, ButtonHeight,
                () => { _selectedIndex = index; Render(); },
                index == _selectedIndex ? UiPanel.SelectedColor : UiPanel.MutedButtonColor);
        }
        y += (float)Math.Ceiling(_components.Length / (double)componentColumns) * (ButtonHeight + Gap) + 8f;

        var selected = _selectedIndex < _components.Length ? _components[_selectedIndex] : null;
        if (selected == null)
            return;

        var selectedName = PickedElement.ComponentTypeName(selected);
        var fields = ComponentInspector.Describe(selected);

        _panel.Label(selectedName, 0, y, width, 20, 14, TextAnchor.MiddleLeft, null, FontStyle.Bold);
        y += 22f;

        // No clipboard API is safe to call here (GUIUtility/IMGUI isn't referenced by this project
        // since some games strip that module) - an editable field lets the user click in, Ctrl+A,
        // Ctrl+C the dump using the field's own native text handling instead.
        _panel.Input(BuildDump(selectedName, fields), 0, y, width, RowHeight, _ => { });
        y += RowHeight + 8f;

        foreach (var field in fields)
        {
            _panel.Label(field.Name, 0, y, 160, RowHeight, 12, TextAnchor.MiddleLeft, UiPanel.DimTextColor);
            _panel.Label(field.Value, 164, y, width - 164, RowHeight, 12, TextAnchor.MiddleLeft);
            y += RowHeight + 2f;
        }

        if (fields.Count == 0)
        {
            _panel.Label($"No built-in inspector for '{selectedName}' - likely a custom game script. " +
                         "Its type name alone may still tell you whether it's game code.",
                0, y, width, 40, 12, TextAnchor.UpperLeft, UiPanel.DimTextColor);
            y += 44f;
        }

        if (UiCompat.As<ContentSizeFitter>(selected) != null || UiCompat.As<HorizontalOrVerticalLayoutGroup>(selected) != null)
        {
            _panel.Label("ContentSizeFitter/LayoutGroup fields are editable from the Layout tab " +
                         "(they're rule fields like anchors/size, not edited here).",
                0, y, width, 32, 12, TextAnchor.UpperLeft, UiPanel.DimTextColor);
        }
    }

    private static string BuildDump(string componentName, List<ComponentField> fields)
    {
        var text = new StringBuilder();
        text.Append(componentName).Append(" | ");
        for (var i = 0; i < fields.Count; i++)
        {
            if (i > 0)
                text.Append(" | ");
            text.Append(fields[i].Name).Append(": ").Append(fields[i].Value);
        }
        return text.ToString();
    }
}

/// <summary>A single named field/value row shown for a component.</summary>
internal struct ComponentField
{
    public readonly string Name;
    public readonly string Value;

    public ComponentField(string name, string value)
    {
        Name = name;
        Value = value;
    }
}

/// <summary>
/// Describes the field values of the component types most relevant to diagnosing layout/sizing
/// issues. Deliberately a curated, typed list rather than generic reflection: under IL2CPP, an
/// arbitrary component's C# runtime type is the generic "Component" wrapper (see
/// <see cref="PickedElement.ComponentTypeName"/>), so blind reflection would only ever see
/// Component's own members, not a HorizontalLayoutGroup's spacing or a LayoutElement's
/// preferredWidth. Casting to a specific, compile-time-known type (via <see cref="UiCompat.As{T}"/>)
/// is the same safe, already-proven pattern used throughout this codebase.
/// </summary>
internal static class ComponentInspector
{
    public static List<ComponentField> Describe(Component component)
    {
        var fields = new List<ComponentField>();
        if (component == null)
            return fields;

        DescribeRectTransform(component, fields);
        DescribeLayoutElement(component, fields);
        DescribeContentSizeFitter(component, fields);
        DescribeLayoutGroup(component, fields);
        DescribeImage(component, fields);
        DescribeText(component, fields);
        DescribeCanvasGroup(component, fields);
        return fields;
    }

    private static void DescribeRectTransform(Component component, List<ComponentField> fields)
    {
        var rect = UiCompat.As<RectTransform>(component);
        if (rect == null)
            return;

        fields.Add(new ComponentField("anchorMin", rect.anchorMin.ToString()));
        fields.Add(new ComponentField("anchorMax", rect.anchorMax.ToString()));
        fields.Add(new ComponentField("pivot", rect.pivot.ToString()));
        fields.Add(new ComponentField("sizeDelta", rect.sizeDelta.ToString()));
        fields.Add(new ComponentField("anchoredPosition", rect.anchoredPosition.ToString()));
        fields.Add(new ComponentField("rect (computed)", rect.rect.ToString()));
        fields.Add(new ComponentField("localEulerAngles", rect.localEulerAngles.ToString()));
    }

    private static void DescribeLayoutElement(Component component, List<ComponentField> fields)
    {
        var element = UiCompat.As<LayoutElement>(component);
        if (element == null)
            return;

        fields.Add(new ComponentField("ignoreLayout", element.ignoreLayout.ToString()));
        fields.Add(new ComponentField("minWidth/minHeight", $"{element.minWidth} / {element.minHeight}"));
        fields.Add(new ComponentField("preferredWidth/Height", $"{element.preferredWidth} / {element.preferredHeight}"));
        fields.Add(new ComponentField("flexibleWidth/Height", $"{element.flexibleWidth} / {element.flexibleHeight}"));
    }

    private static void DescribeContentSizeFitter(Component component, List<ComponentField> fields)
    {
        var fitter = UiCompat.As<ContentSizeFitter>(component);
        if (fitter == null)
            return;

        fields.Add(new ComponentField("enabled", fitter.enabled.ToString()));
        fields.Add(new ComponentField("horizontalFit", fitter.horizontalFit.ToString()));
        fields.Add(new ComponentField("verticalFit", fitter.verticalFit.ToString()));
    }

    private static void DescribeLayoutGroup(Component component, List<ComponentField> fields)
    {
        var group = UiCompat.As<LayoutGroup>(component);
        if (group == null)
            return;

        fields.Add(new ComponentField("enabled", group.enabled.ToString()));
        fields.Add(new ComponentField("padding", $"L{group.padding.left} R{group.padding.right} T{group.padding.top} B{group.padding.bottom}"));
        fields.Add(new ComponentField("childAlignment", group.childAlignment.ToString()));

        var horizontal = UiCompat.As<HorizontalOrVerticalLayoutGroup>(component);
        if (horizontal != null)
        {
            fields.Add(new ComponentField("spacing", horizontal.spacing.ToString()));
            fields.Add(new ComponentField("childControlWidth/Height", $"{horizontal.childControlWidth} / {horizontal.childControlHeight}"));
            fields.Add(new ComponentField("childForceExpandWidth/Height", $"{horizontal.childForceExpandWidth} / {horizontal.childForceExpandHeight}"));
            fields.Add(new ComponentField("childScaleWidth/Height", $"{horizontal.childScaleWidth} / {horizontal.childScaleHeight}"));
            return;
        }

        var grid = UiCompat.As<GridLayoutGroup>(component);
        if (grid != null)
        {
            fields.Add(new ComponentField("cellSize", grid.cellSize.ToString()));
            fields.Add(new ComponentField("spacing", grid.spacing.ToString()));
            fields.Add(new ComponentField("constraint", grid.constraint.ToString()));
        }
    }

    private static void DescribeImage(Component component, List<ComponentField> fields)
    {
        var image = UiCompat.As<Image>(component);
        if (image == null)
            return;

        fields.Add(new ComponentField("enabled", image.enabled.ToString()));
        fields.Add(new ComponentField("sprite", image.sprite != null ? image.sprite.name : "(none)"));
        fields.Add(new ComponentField("type", image.type.ToString()));
        fields.Add(new ComponentField("preserveAspect", image.preserveAspect.ToString()));
    }

    private static void DescribeText(Component component, List<ComponentField> fields)
    {
        var text = UiCompat.As<Text>(component);
        if (text == null)
            return;

        fields.Add(new ComponentField("text", PickedElement.Truncate(text.text, 60)));
        fields.Add(new ComponentField("fontSize", text.fontSize.ToString()));
        fields.Add(new ComponentField("resizeTextForBestFit", text.resizeTextForBestFit.ToString()));
        fields.Add(new ComponentField("horizontal/verticalOverflow", $"{text.horizontalOverflow} / {text.verticalOverflow}"));
    }

    private static void DescribeCanvasGroup(Component component, List<ComponentField> fields)
    {
        var group = UiCompat.As<CanvasGroup>(component);
        if (group == null)
            return;

        fields.Add(new ComponentField("alpha", group.alpha.ToString()));
        fields.Add(new ComponentField("interactable", group.interactable.ToString()));
        fields.Add(new ComponentField("blocksRaycasts", group.blocksRaycasts.ToString()));
    }
}
