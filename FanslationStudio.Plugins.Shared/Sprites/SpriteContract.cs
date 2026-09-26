namespace FanslationStudio.Plugins.Sprites;

/// <summary>
/// One sprite replacement rule, stored in BepInEx/sprites2/*.yaml. The replacement image is
/// BepInEx/sprites2/dumped/&lt;replacementSprite&gt;.png - the same folder dumps are written to, so
/// the workflow is: dump, edit the PNG in place, reload.
/// </summary>
public record SpriteContract
{
    /// <summary>Hierarchy path of the Image, with the same wildcard rules as layouts/resizers.
    /// Use "/*" with <see cref="OriginalSprite"/> to replace a sprite wherever it appears.</summary>
    public string Path = string.Empty;

    /// <summary>File name (without .png) in sprites2/dumped/.</summary>
    public string ReplacementSprite = string.Empty;

    /// <summary>Only replace while the Image shows the sprite with this name. Leave empty to
    /// replace whatever sprite the Image shows (games that swap icons on one Image need this set).</summary>
    public string OriginalSprite;

    /// <summary>Free-text description.</summary>
    public string Name;

    /// <summary>Set to false to keep the rule in the file without applying it.</summary>
    public bool? Enabled;

    // Methods rather than properties: YamlDotNet serializes read-only properties.
    public bool IsEnabled() => Enabled != false;

    /// <summary>Unique key: several rules may share a path if they replace different sprites.</summary>
    public static string KeyOf(SpriteContract contract) =>
        string.IsNullOrEmpty(contract.OriginalSprite) ? contract.Path : $"{contract.Path} [{contract.OriginalSprite}]";

    /// <summary>A file-name-safe version of a sprite name, used for dumped PNGs.</summary>
    public static string SafeFileName(string name)
    {
        if (string.IsNullOrEmpty(name))
            return "sprite";

        var chars = name.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            var c = chars[i];
            if (c == '/' || c == '\\' || c == ':' || c == '*' || c == '?' || c == '"' || c == '<' || c == '>' || c == '|' || c < 32)
                chars[i] = '_';
        }
        return new string(chars).Trim();
    }
}
