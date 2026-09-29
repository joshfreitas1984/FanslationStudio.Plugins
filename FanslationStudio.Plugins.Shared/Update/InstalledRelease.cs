using System.IO;
using System.Text.RegularExpressions;

namespace FanslationStudio.Plugins.Update;

/// <summary>
/// What the installer/updater recorded about the installed patch: BepInEx/release-manifest.json
/// (written by the packager in FanslationStudio.LlmKit.Release). Carries the release metadata the
/// updater needs, so no per-game plugin config is required.
/// </summary>
public sealed class InstalledRelease
{
    public const string ManifestFileName = "release-manifest.json";

    public string Version { get; private set; }
    public string GitHubRepo { get; private set; }
    public int SteamAppId { get; private set; }
    public string PatchZipPrefix { get; private set; }

    /// <summary>Null when no manifest is installed (e.g. a manual install) or it has no version.</summary>
    public static InstalledRelease Read(string bepInExRoot)
    {
        var path = Path.Combine(bepInExRoot, ManifestFileName);
        return File.Exists(path) ? Parse(File.ReadAllText(path)) : null;
    }

    // The manifest is our own flat, machine-written JSON and these fields are plain strings/numbers, so a
    // targeted match avoids shipping a JSON library into every game's plugin folder.
    public static InstalledRelease Parse(string json)
    {
        var version = StringField(json, "version");
        if (string.IsNullOrEmpty(version))
            return null;

        int.TryParse(NumberField(json, "steamAppId"), out var appId);
        return new InstalledRelease
        {
            Version = version,
            GitHubRepo = StringField(json, "gitHubRepo"),
            SteamAppId = appId,
            PatchZipPrefix = StringField(json, "patchZipPrefix"),
        };
    }

    private static string StringField(string json, string name)
    {
        var match = Regex.Match(json, "\"" + name + "\"\\s*:\\s*\"([^\"\\\\]*)\"");
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string NumberField(string json, string name)
    {
        var match = Regex.Match(json, "\"" + name + "\"\\s*:\\s*(\\d+)");
        return match.Success ? match.Groups[1].Value : null;
    }
}
