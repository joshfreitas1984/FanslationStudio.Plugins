using System;
using System.IO;
using FanslationStudio.Plugins.Layout;
using FanslationStudio.Plugins.Shared;
using FanslationStudio.Plugins.Sprites;
using FanslationStudio.Plugins.Support;
using FanslationStudio.Plugins.TextResizer;
using FanslationStudio.Plugins.UnityShared.Layout;
using FanslationStudio.Plugins.UnityShared.Sprites;

namespace FanslationStudio.Plugins.UnityShared.Editor;

/// <summary>
/// Host-agnostic entry point for the UI Editor. Each BepInEx host plugin calls
/// <see cref="Initialize"/> once, then <see cref="Tick"/> once per frame from whatever tick
/// mechanism is safe on that runtime.
/// </summary>
internal static class UiEditorHost
{
    private static IPluginLogger _logger;
    private static UiEditorHotkeys _hotkeys;
    private static string _harmonyId;
    private static bool _layoutsEnabled;
    private static bool _spritesEnabled;
    private static string _lastError;

    public static bool Enabled { get; private set; }

    public static void Initialize(IPluginLogger logger, BepInEx.Configuration.ConfigFile config, IYamlHelper yamlHelper,
        string bepInExRootPath, string harmonyId)
    {
        _logger = logger;
        _harmonyId = harmonyId;

        Enabled = config.Bind("General", "Enabled", true, "Enable the UI Editor").Value;
        if (!Enabled)
            return;

        _layoutsEnabled = config.Bind("Layouts", "Enabled", true,
            "Apply layout rules from BepInEx/layouts/*.yaml").Value;
        _spritesEnabled = config.Bind("Sprites", "Enabled", true,
            "Apply sprite replacements from BepInEx/sprites2/*.yaml (images in sprites2/dumped/)").Value;

        _hotkeys = UiEditorHotkeys.Bind(config);
        PickerController.Configure(logger, _hotkeys);

        var windowScale = config.Bind("Editor", "WindowScale", 1f,
            "Size multiplier for the editor window (e.g. 1.5 on 4K screens)").Value;
        var openOnPick = config.Bind("Editor", "OpenOnPick", true,
            "Open the editor window automatically when you pick an element").Value;
        EditorWindow.Configure(windowScale);
        EditorSettings.AutoSave = config.Bind("Editor", "AutoSave", true,
            "Save changed rules automatically when you move to another element, switch tab or close the window").Value;
        if (openOnPick)
            PickerController.Picked += EditorWindow.Open;

        if (_layoutsEnabled)
        {
            var repository = new ContractRepository<LayoutContract>(
                Path.Combine(bepInExRootPath, "layouts"), "zzAddedLayouts.yaml", yamlHelper, logger, c => c.Path);
            LayoutApplier.Initialize(repository, logger);
        }

        if (_spritesEnabled)
        {
            var repository = new ContractRepository<SpriteContract>(
                Path.Combine(bepInExRootPath, "sprites2"), "zzAdded.yaml", yamlHelper, logger, c => c.Path, SpriteContract.KeyOf);
            SpriteApplier.Initialize(repository, logger);
        }

        _logger.LogInfo("[UIEditor] Loaded.");
    }

    /// <summary>Never throws: tick callbacks are engine events that other listeners share.</summary>
    public static void Tick()
    {
        if (!Enabled)
            return;

        try
        {
            if (_layoutsEnabled || _spritesEnabled)
                UiHooks.EnsurePatched(_harmonyId + ".Hooks", _logger);

            if (_hotkeys.Reload.IsDown())
                ReloadAll();

            if (_layoutsEnabled)
                LayoutApplier.Tick();
            if (_spritesEnabled)
                SpriteApplier.Tick();

            if (_hotkeys.ToggleWindow.IsDown() && !EditorWindow.IsTyping)
                EditorWindow.Toggle();

            PickerController.Tick();
            EditorWindow.Tick();
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

    /// <summary>Reloads layouts, sprites and resizers from disk and re-applies them.</summary>
    public static void ReloadAll()
    {
        if (_layoutsEnabled)
            LayoutApplier.Reload();
        if (_spritesEnabled)
            SpriteApplier.Reload();

        var resizers = TextResizerService.Instance;
        if (resizers != null)
        {
            resizers.LoadResizers();
            // Revert-and-reapply everything, so removed resizers don't leave values behind.
            resizers.RefreshMatching(new[] { "/*" });
        }

        EditorWindow.SetStatus("Reloaded layouts, sprites and resizers from disk.");
    }
}
