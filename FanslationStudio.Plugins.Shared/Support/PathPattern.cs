using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;

namespace FanslationStudio.Plugins.Support;

/// <summary>
/// Hierarchy path matching shared by the resizer, sprite and layout engines.
///
/// Rules:
///   * <c>*</c> matches zero or more characters, including <c>/</c> (so it can span levels).
///   * Patterns are anchored to the whole path - <c>Canvas/*/Title</c> does not match
///     <c>Canvas/Panel/Title/Child</c>. Add a trailing <c>*</c> if you want children too.
///   * A leading <c>/</c> means "at any depth": <c>/Title/Text</c> matches any path ending in
///     <c>Title/Text</c>, and <c>/*</c> matches everything (the global resizer convention).
///   * Every other character is literal (<c>[UI]</c>, <c>Text (TMP)</c>, <c>a.b</c> etc).
/// </summary>
public static class PathPattern
{
    private static readonly ConcurrentDictionary<string, Regex> CompiledCache = new();

    public static bool IsWildcard(string pattern)
    {
        return pattern != null && (pattern.IndexOf('*') >= 0 || (pattern.Length > 0 && pattern[0] == '/'));
    }

    /// <summary>
    /// The name every path matching this pattern must end with (its last segment), or null if
    /// that segment contains a <c>*</c> and so can match any name. Lets hot hooks skip building
    /// a hierarchy path for objects whose own name can't match any rule.
    /// </summary>
    public static string LeafNameOf(string pattern)
    {
        if (pattern == null)
            return null;

        var leaf = pattern.Substring(pattern.LastIndexOf('/') + 1);
        return leaf.IndexOf('*') >= 0 ? null : leaf;
    }

    public static bool IsMatch(string pattern, string path)
    {
        if (pattern == null || path == null)
            return false;

        if (!IsWildcard(pattern))
            return pattern == path;

        return CompiledCache.GetOrAdd(pattern, BuildRegex).IsMatch(path);
    }

    internal static Regex BuildRegex(string pattern)
    {
        var builder = new StringBuilder("^");
        var body = pattern;

        if (body.StartsWith("/", System.StringComparison.Ordinal))
        {
            builder.Append("(?:.*/)?");
            body = body.TrimStart(new[] { '/' });
        }

        var segments = body.Split(new[] { '*' });
        for (var i = 0; i < segments.Length; i++)
        {
            if (i > 0)
                builder.Append(".*");
            builder.Append(Regex.Escape(segments[i]));
        }

        builder.Append('$');
        return new Regex(builder.ToString(), RegexOptions.Compiled | RegexOptions.CultureInvariant);
    }
}
