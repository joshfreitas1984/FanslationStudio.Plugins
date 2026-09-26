namespace FanslationStudio.Plugins.Layout;

/// <summary>
/// One layout rule, stored in BepInEx/layouts/*.yaml. Every field is optional: anything left out
/// keeps the game's value. Vectors are [x, y] (or [x, y, z] for localPosition/localScale).
///
/// Absolute fields (anchoredPosition, sizeDelta, ...) set a value outright. Offset fields
/// (offsetPosition, offsetSize) add to the element's original value, or to the absolute value if
/// both are set. Values are always computed from the originals captured the first time a rule
/// touches an element, so re-applying a rule never drifts.
/// </summary>
public record LayoutContract
{
    /// <summary>Hierarchy path, with the same wildcard rules as resizers (see PathPattern).</summary>
    public string Path = string.Empty;

    /// <summary>Free-text description, e.g. what the rule is for.</summary>
    public string Name;

    /// <summary>Set to false to keep the rule in the file without applying it.</summary>
    public bool? Enabled;

    /// <summary>Re-apply every frame, for elements the game keeps repositioning.</summary>
    public bool? Enforce;

    public bool? Active;

    public float[] AnchorMin;
    public float[] AnchorMax;
    public float[] Pivot;
    public float[] AnchoredPosition;
    public float[] OffsetPosition;
    public float[] SizeDelta;
    public float[] OffsetSize;
    public float[] LocalPosition;
    public float[] LocalScale;
    public float? RotationZ;

    /// <summary>
    /// With RotationZ set, counter-rotates every direct child by the opposite angle so it stays
    /// upright regardless of how this element rotates - e.g. rotate a diamond-shaped icon
    /// background without its label rotating along with it. A child with its own RotationZ rule
    /// is left alone; that rule wins.
    /// </summary>
    public bool? CounterRotateChildren;

    /// <summary>
    /// With CounterRotateChildren and a RotationZ of 90 or 270 degrees, also swaps each
    /// counter-rotated child's sizeDelta x/y and suspends any ContentSizeFitter on it - Unity's
    /// layout system never accounts for transform rotation when sizing, so a child sized for a
    /// horizontal layout keeps that width even once it's rotated upright again.
    /// </summary>
    public bool? CounterRotateSwapSize;

    /// <summary>Copies anchors, pivot and position from another element, relative to this one
    /// (e.g. "../HeroName"). Applied before the fields above.</summary>
    public string CopyRectFrom;
    public bool? CopySizeFromSource;

    /// <summary>Moves this element immediately before a sibling in draw order (e.g. "../HeroName").</summary>
    public string PlaceBefore;

    public bool? ImageEnabled;
    public bool? PreserveAspect;
    public bool? ContentSizeFitterEnabled;
    /// <summary>Enables/disables any Horizontal/Vertical/Grid layout group on the element.</summary>
    public bool? LayoutGroupEnabled;

    /// <summary>ContentSizeFitter.FitMode by name ("Unconstrained", "MinSize", "PreferredSize").</summary>
    public string ContentSizeFitterHorizontal;
    public string ContentSizeFitterVertical;

    /// <summary>Any Horizontal/VerticalLayoutGroup on the element (ignored on a GridLayoutGroup).</summary>
    public bool? LayoutGroupChildControlWidth;
    public bool? LayoutGroupChildControlHeight;
    public bool? LayoutGroupChildForceExpandWidth;
    public bool? LayoutGroupChildForceExpandHeight;
    public float? LayoutGroupSpacing;

    // Methods rather than properties: YamlDotNet serializes read-only properties.
    public bool IsEnabled() => Enabled != false;
    public bool IsEnforced() => Enforce == true;

    public LayoutContract DeepCopy()
    {
        var copy = this with { };
        copy.AnchorMin = CloneArray(AnchorMin);
        copy.AnchorMax = CloneArray(AnchorMax);
        copy.Pivot = CloneArray(Pivot);
        copy.AnchoredPosition = CloneArray(AnchoredPosition);
        copy.OffsetPosition = CloneArray(OffsetPosition);
        copy.SizeDelta = CloneArray(SizeDelta);
        copy.OffsetSize = CloneArray(OffsetSize);
        copy.LocalPosition = CloneArray(LocalPosition);
        copy.LocalScale = CloneArray(LocalScale);
        return copy;
    }

    private static float[] CloneArray(float[] value) => value == null ? null : (float[])value.Clone();
}
