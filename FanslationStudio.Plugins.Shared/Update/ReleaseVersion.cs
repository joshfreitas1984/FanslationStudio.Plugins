namespace FanslationStudio.Plugins.Update;

/// <summary>Compares dotted numeric release versions such as 2026.09.29.18.07.</summary>
public static class ReleaseVersion
{
    /// <summary>True when <paramref name="latest"/> is newer. A missing installed version is always older.</summary>
    public static bool IsNewer(string installed, string latest)
    {
        if (string.IsNullOrWhiteSpace(installed))
            return !string.IsNullOrWhiteSpace(latest);

        var a = Parse(installed);
        var b = Parse(latest);
        if (a == null || b == null)
            return !string.Equals(installed.Trim(), latest?.Trim(), System.StringComparison.OrdinalIgnoreCase);

        var length = a.Length > b.Length ? a.Length : b.Length;
        for (var i = 0; i < length; i++)
        {
            var x = i < a.Length ? a[i] : 0;
            var y = i < b.Length ? b[i] : 0;
            if (x != y)
                return y > x;
        }

        return false;
    }

    private static long[] Parse(string version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return null;

        // Not TrimStart(char) / Split(char): older Unity Mono runtimes lack those overloads.
        var parts = version.Trim().TrimStart(new[] { 'v', 'V' }).Split(new[] { '.' });
        var numbers = new long[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!long.TryParse(parts[i], out numbers[i]))
                return null;
        }

        return numbers;
    }
}
