using System;
using FanslationStudio.Plugins.Shared;

namespace FanslationStudio.Plugins.UnityShared.Editor;

/// <summary>
/// Host-agnostic entry point for the UI Editor. Each BepInEx host plugin binds config, calls
/// <see cref="Initialize"/>, and calls <see cref="Tick"/> once per frame from whatever tick
/// mechanism is safe on that runtime.
/// </summary>
internal static class UiEditorHost
{
    private static IPluginLogger _logger;
    private static string _lastError;

    public static bool Enabled { get; private set; }

    public static void Initialize(IPluginLogger logger, BepInEx.Configuration.ConfigFile config)
    {
        _logger = logger;
        Enabled = config.Bind("General", "Enabled", true, "Enable the UI Editor (element picker)").Value;
        if (!Enabled)
            return;

        PickerController.Configure(logger, PickerHotkeys.Bind(config));
        _logger.LogInfo("[UIEditor] Loaded.");
    }

    /// <summary>Never throws: tick callbacks are engine events that other listeners share.</summary>
    public static void Tick()
    {
        if (!Enabled)
            return;

        try
        {
            PickerController.Tick();
            _lastError = null;
        }
        catch (Exception ex)
        {
            // Log each distinct failure once instead of every frame.
            var message = ex.ToString();
            if (message != _lastError)
                _logger?.LogError($"[UIEditor] Tick threw: {message}");
            _lastError = message;
        }
    }
}
