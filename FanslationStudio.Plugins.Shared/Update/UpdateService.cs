using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using FanslationStudio.Plugins.Shared;

namespace FanslationStudio.Plugins.Update;

public enum UpdateStatus
{
    Idle,
    Checking,
    UpToDate,
    Available,
    Downloading,
    ReadyToApply,
    Failed,
}

/// <summary>
/// Checks GitHub for a newer patch release, downloads it plus the latest installer, and launches the
/// installer's --apply-update mode. Pure .NET (no Unity calls) and safe to run from background tasks:
/// the UI polls <see cref="Status"/> from the game's tick. Nothing here throws into the caller and a
/// failed check or download never affects the running game.
/// </summary>
public sealed class UpdateService
{
    // Mirrors FanslationStudio.Installer.Core.InstallerAssets: the rolling "installer" pre-release keeps a
    // fixed URL, so the updater is always the latest build and never has to ship inside the patch zip.
    private const string InstallerTag = "installer";
    private const string InstallerFileName = "Installer-win-x64.exe";
    private const string DefaultZipPrefix = "EnglishPatch";

    private readonly IPluginLogger _logger;
    private readonly HttpClient _http;
    private readonly string _stagingFolder;

    private volatile UpdateStatus _status = UpdateStatus.Idle;
    private volatile string _latestVersion;
    private volatile string _error;
    private volatile int _progress;
    private volatile string _latestTag;
    private string _zipPath;
    private string _installerPath;

    public UpdateService(IPluginLogger logger, HttpClient http, string stagingFolder)
    {
        _logger = logger;
        _http = http;
        _stagingFolder = stagingFolder;
    }

    public UpdateStatus Status => _status;
    public string LatestVersion => _latestVersion;
    public string Error => _error;

    /// <summary>Download progress, 0-100.</summary>
    public int Progress => _progress;

    public InstalledRelease Installed { get; private set; }

    public static string BuildLatestReleaseApiUrl(string ownerAndRepo) =>
        "https://api.github.com/repos/" + ownerAndRepo + "/releases/latest";

    public static string BuildPatchZipUrl(string ownerAndRepo, string prefix, string tag) =>
        "https://github.com/" + ownerAndRepo + "/releases/download/" + tag + "/" +
        (string.IsNullOrEmpty(prefix) ? DefaultZipPrefix : prefix) + "-" + tag.TrimStart(new[] { 'v', 'V' }) + ".zip";

    public static string BuildInstallerUrl(string ownerAndRepo) =>
        "https://github.com/" + ownerAndRepo + "/releases/download/" + InstallerTag + "/" + InstallerFileName;

    public static string ParseTagName(string releaseJson)
    {
        var match = Regex.Match(releaseJson ?? string.Empty, "\"tag_name\"\\s*:\\s*\"([^\"]+)\"");
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>Starts a background check; poll <see cref="Status"/> for Available / UpToDate / Failed.</summary>
    public void StartCheck(InstalledRelease installed)
    {
        Installed = installed;
        if (_status != UpdateStatus.Idle && _status != UpdateStatus.UpToDate && _status != UpdateStatus.Failed)
            return;

        _status = UpdateStatus.Checking;
        Task.Run(() => CheckAsync(installed));
    }

    /// <summary>Starts downloading the patch zip and installer; poll <see cref="Status"/> for ReadyToApply / Failed.</summary>
    public void StartDownload()
    {
        if (_status != UpdateStatus.Available)
            return;

        _status = UpdateStatus.Downloading;
        _progress = 0;
        Task.Run(DownloadAsync);
    }

    private async Task CheckAsync(InstalledRelease installed)
    {
        try
        {
            using (var request = new HttpRequestMessage(HttpMethod.Get, BuildLatestReleaseApiUrl(installed.GitHubRepo)))
            {
                request.Headers.UserAgent.Add(new ProductInfoHeaderValue("FanslationStudio-Plugins", "1.0"));
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

                using (var response = await _http.SendAsync(request).ConfigureAwait(false))
                {
                    // No published release yet: nothing to update to.
                    if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                    {
                        _status = UpdateStatus.UpToDate;
                        return;
                    }

                    response.EnsureSuccessStatusCode();
                    var tag = ParseTagName(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
                    if (tag == null)
                        throw new InvalidDataException("The latest release has no tag_name.");

                    // Set before the status flips: the UI reads them as soon as it sees Available.
                    _latestTag = tag;
                    _latestVersion = tag.TrimStart(new[] { 'v', 'V' });
                    _status = ReleaseVersion.IsNewer(installed.Version, _latestVersion) ? UpdateStatus.Available : UpdateStatus.UpToDate;
                    _logger.LogInfo("[Updater] Installed " + installed.Version + ", latest " + _latestVersion + " -> " + _status);
                }
            }
        }
        catch (Exception ex)
        {
            Fail("Update check failed: " + ex.Message);
        }
    }

    private async Task DownloadAsync()
    {
        try
        {
            // Temp, not the game folder: the installer copy must not sit inside the folder it is about to overwrite.
            if (Directory.Exists(_stagingFolder))
                Directory.Delete(_stagingFolder, true);
            Directory.CreateDirectory(_stagingFolder);

            var repo = Installed.GitHubRepo;
            _zipPath = Path.Combine(_stagingFolder, Path.GetFileName(BuildPatchZipUrl(repo, Installed.PatchZipPrefix, _latestTag)));
            _installerPath = Path.Combine(_stagingFolder, InstallerFileName);

            await DownloadFileAsync(BuildPatchZipUrl(repo, Installed.PatchZipPrefix, _latestTag), _zipPath, 0, 50).ConfigureAwait(false);
            await DownloadFileAsync(BuildInstallerUrl(repo), _installerPath, 50, 100).ConfigureAwait(false);

            _progress = 100;
            _status = UpdateStatus.ReadyToApply;
        }
        catch (Exception ex)
        {
            Fail("Download failed, your install is unchanged: " + ex.Message);
        }
    }

    private async Task DownloadFileAsync(string url, string destination, int progressFrom, int progressTo)
    {
        var partial = destination + ".part";
        using (var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? 0;

            using (var source = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
            using (var target = File.Create(partial))
            {
                var buffer = new byte[81920];
                long done = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
                {
                    await target.WriteAsync(buffer, 0, read).ConfigureAwait(false);
                    done += read;
                    if (total > 0)
                        _progress = progressFrom + (int)((progressTo - progressFrom) * done / total);
                }
            }
        }

        if (File.Exists(destination))
            File.Delete(destination);
        File.Move(partial, destination);
    }

    /// <summary>
    /// Starts the downloaded installer in --apply-update mode (detached). The caller then quits the game; the
    /// updater waits for <paramref name="gamePid"/> to exit before touching any file. Returns false on failure.
    /// </summary>
    public bool TryLaunchUpdater(int gamePid, string gameDir)
    {
        try
        {
            if (_status != UpdateStatus.ReadyToApply || !File.Exists(_installerPath) || !File.Exists(_zipPath))
                return false;

            var args = "--apply-update " + Quote(_zipPath) + " --pid " + gamePid + " --game-dir " + Quote(gameDir);
            if (Installed.SteamAppId > 0)
                args += " --steam-app-id " + Installed.SteamAppId;

            Process.Start(new ProcessStartInfo(_installerPath, args)
            {
                UseShellExecute = false,
                WorkingDirectory = _stagingFolder,
            });
            _logger.LogInfo("[Updater] Launched updater for " + _latestVersion);
            return true;
        }
        catch (Exception ex)
        {
            Fail("Could not start the updater, your install is unchanged: " + ex.Message);
            return false;
        }
    }

    private static string Quote(string value) => "\"" + value.TrimEnd(new[] { '\\' }) + "\"";

    private void Fail(string message)
    {
        _error = message;
        _status = UpdateStatus.Failed;
        _logger.LogWarning("[Updater] " + message);
    }
}
