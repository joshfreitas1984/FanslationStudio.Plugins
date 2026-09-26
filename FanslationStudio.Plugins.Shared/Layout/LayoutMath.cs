namespace FanslationStudio.Plugins.Layout;

/// <summary>Unity-free vector arithmetic for layout rules, so it can be unit tested.</summary>
public static class LayoutMath
{
    /// <summary>
    /// Resolves an absolute value and/or an offset against a base (original) value.
    /// Returns null when neither is set, meaning "leave the property alone".
    /// Invalid vectors (wrong length) are treated as not set.
    /// </summary>
    public static float[] Resolve(float[] absolute, float[] offset, float[] baseValue)
    {
        var dimensions = baseValue.Length;
        var hasAbsolute = IsValid(absolute, dimensions);
        var hasOffset = IsValid(offset, dimensions);

        if (!hasAbsolute && !hasOffset)
            return null;

        var result = new float[dimensions];
        for (var i = 0; i < dimensions; i++)
        {
            result[i] = hasAbsolute ? absolute[i] : baseValue[i];
            if (hasOffset)
                result[i] += offset[i];
        }

        return result;
    }

    public static bool IsValid(float[] value, int dimensions)
    {
        return value != null && value.Length == dimensions;
    }

    /// <summary>A vector field that is set but has the wrong number of components.</summary>
    public static bool IsMalformed(float[] value, int dimensions)
    {
        return value != null && value.Length != dimensions;
    }
}
