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

    // Methods rather than properties: YamlDotNet serializes read-only properties.
    public bool IsEnabled() => Enabled != false;
    public bool IsEnforced() => Enforce == true;
}
