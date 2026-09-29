using System;
using System.IO;
using System.Net;
using System.Net.Http;
using FanslationStudio.Plugins.Shared;
using FanslationStudio.Plugins.Update;

namespace FanslationStudio.Plugins.UnityShared.Update;

/// <summary>
/// Host-agnostic entry point for the in-game update check. A host plugin calls <see cref="Initialize"/>
/// once from Load (only pure .NET happens there: config, file read, a background HTTP task) and
/// <see cref="Tick"/> once per frame from whatever tick mechanism is safe on that runtime.
/// The repo, Steam app ID and zip name come from the installed release-manifest.json, so a game needs no
/// updater config; a manual install with no manifest simply never prompts.
/// </summary>
internal static class UpdateHost
{
    private static IPluginLogger _logger;
    private static UpdateService _service;
    private static string _gameDir;
    private static DateTime _showAfterUtc;
    private static string _lastError;

    public static bool Enabled { get; private set; }

    public static void Initialize(IPluginLogger logger, string bepInExRootPath, string gameRootPath)
    {
        _logger = logger;
        var config = PluginConfig.File;

        var enabled = config.Bind("Updates", "Enabled", true,
            "Check GitHub for a newer patch when the game starts and offer to install it").Value;
        var promptDelay = config.Bind("Updates", "PromptDelaySeconds", 15,
            "Seconds to wait after startup before showing the update prompt, so it doesn't appear over the loading screen").Value;
        if (!enabled)
            return;

        try
        {
            var installed = InstalledRelease.Read(bepInExRootPath);
            if (installed == null || string.IsNullOrEmpty(installed.GitHubRepo))
            {
                _logger.LogInfo("[Updater] No installed release info (BepInEx/" + InstalledRelease.ManifestFileName +
                                " with a gitHubRepo); update checks are off.");
                return;
            }

            var scale = config.Bind("Editor", "WindowScale", 1f,
                "Size multiplier for the editor window (e.g. 1.5 on 4K screens)").Value;
            UpdatePrompt.Configure(scale);

#if !IL2CPP
            // Old Unity Mono can default to TLS 1.0, which api.github.com rejects. 3072 is Tls12 (the enum
            // member is missing from some old reference assemblies).
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
#endif

            _gameDir = gameRootPath;
            _showAfterUtc = DateTime.UtcNow.AddSeconds(Math.Max(0, promptDelay));
            _service = new UpdateService(logger,
                new HttpClient { Timeout = TimeSpan.FromMinutes(10) },
                Path.Combine(Path.GetTempPath(), "FanslationStudio-update"));
            _service.StartCheck(installed);
            Enabled = true;
        }
        catch (Exception ex)
        {
            // An updater problem must never stop the translation from loading.
            _logger.LogWarning("[Updater] Could not start the update check: " + ex.Message);
        }
    }

    /// <summary>Never throws: tick callbacks are engine events that other listeners share.</summary>
    public static void Tick()
    {
        if (!Enabled)
            return;

        try
        {
            UpdatePrompt.Tick(_service, _gameDir, _showAfterUtc);
            _lastError = null;
        }
        catch (Exception ex)
        {
            var message = ex.ToString();
            if (message != _lastError)
                _logger?.LogError("[Updater] Tick threw: " + message);
            _lastError = message;
        }
    }
}
