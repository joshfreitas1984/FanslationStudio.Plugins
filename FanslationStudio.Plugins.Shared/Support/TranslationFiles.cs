using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace FanslationStudio.Plugins.Support;

public static class TranslationFiles
{
    /// <summary>
    /// Every file in <paramref name="folder"/> whose name matches <paramref name="filePattern"/>
    /// (a file-name wildcard such as "*dynamicStrings*": * matches anything, ? one character),
    /// ignoring case, in alphabetical order so later files override earlier ones predictably.
    /// Empty if the folder doesn't exist.
    /// </summary>
    public static string[] Find(string folder, string filePattern)
    {
        if (!Directory.Exists(folder))
            return [];

        // Matched here rather than via Directory.GetFiles' own pattern, which is case-sensitive
        // on Linux (Proton/Wine Mono, native Linux builds).
        var pattern = ToRegex(string.IsNullOrEmpty(filePattern) ? "*" : filePattern);
        var result = new List<string>();
        foreach (var file in Directory.GetFiles(folder))
        {
            if (pattern.IsMatch(Path.GetFileName(file)))
                result.Add(file);
        }

        result.Sort(StringComparer.OrdinalIgnoreCase);
        return result.ToArray();
    }

    public static bool IsMatch(string fileName, string filePattern) =>
        ToRegex(string.IsNullOrEmpty(filePattern) ? "*" : filePattern).IsMatch(fileName ?? string.Empty);

    private static Regex ToRegex(string filePattern)
    {
        var regex = "^" + Regex.Escape(filePattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
        return new Regex(regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
