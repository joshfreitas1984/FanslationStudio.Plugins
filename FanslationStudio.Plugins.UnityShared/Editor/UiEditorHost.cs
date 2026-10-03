using System;
using System.IO;
using BepInEx.Configuration;
using FanslationStudio.Plugins.DynamicStrings;
using FanslationStudio.Plugins.Layout;
using FanslationStudio.Plugins.PrefabText;
using FanslationStudio.Plugins.Shared;
using FanslationStudio.Plugins.Sprites;
using FanslationStudio.Plugins.Support;
using FanslationStudio.Plugins.TextResizer;
using FanslationStudio.Plugins.UnityShared.Layout;
using FanslationStudio.Plugins.UnityShared.Sprites;
using UnityEngine;

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
    private static ConfigEntry<string> _foreignLanguagePattern;
    private static ConfigEntry<string> _dumpAssemblies;
    private static string _managedPath;
    private static string _gameDataPath;
    private static IPrefabTextFinder _prefabTextFinder;
    private static string _rawStringsPath;
    private static int _lastTickedFrame = -1;

    public static bool Enabled { get; private set; }

    public static void Initialize(IPluginLogger logger, IYamlHelper yamlHelper,
        string bepInExRootPath, string harmonyId, string managedPath, string gameDataPath, IPrefabTextFinder prefabTextFinder)
    {
        _logger = logger;
        _harmonyId = harmonyId;
        var config = PluginConfig.File;

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
        var autoSave = config.Bind("Editor", "AutoSave", true,
            "Save changed rules automatically when you move to another element, switch tab or close the window");
        EditorSettings.AutoSave = autoSave.Value;
        // Applies straight away when changed from the Plugin settings view.
        autoSave.SettingChanged += (_, _) => EditorSettings.AutoSave = autoSave.Value;
        if (openOnPick)
            PickerController.Picked += EditorWindow.Open;

        // Kept as entries and read at dump time, so edits in the Plugin settings view apply
        // to the next Dump strings press without a restart.
        _foreignLanguagePattern = config.Bind("Dumping", "ForeignLanguagePattern", DynamicStringSupport.ChineseCharPattern,
            "Regex pattern for foreign language text to include when dumping strings");
        _rawStringsPath = Path.Combine(Path.Combine(bepInExRootPath, "plugins"), "rawStrings");
        _dumpAssemblies = config.Bind("Dumping", "Assemblies", StringDumperService.DefaultAssemblyPatterns,
            "Assemblies in the game's Managed folder to scan for dynamic strings, separated by ';' (e.g. Assembly-CSharp.dll;Mortal.*.dll)");
        _managedPath = managedPath;
        _gameDataPath = gameDataPath;
        _prefabTextFinder = prefabTextFinder;

        if (_layoutsEnabled)
        {
            var repository = new ContractRepository<LayoutContract>(
                Path.Combine(bepInExRootPath, "layouts"), "zzAddedLayouts.yaml", yamlHelper, logger, c => c.Path);
            LayoutApplier.Initialize(repository, logger);
        }

        if (_spritesEnabled)
        {
            var repository = new ContractRepository<SpriteContract>(
                Path.Combine(bepInExRootPath, "sprites2"), "zzAddedSprites.yaml", yamlHelper, logger, c => c.Path, SpriteContract.KeyOf);
            SpriteApplier.Initialize(repository, logger);
        }

        _logger.LogInfo("[UIEditor] Loaded.");
    }

    /// <summary>Never throws: tick callbacks are engine events that other listeners share.</summary>
    public static void Tick()
    {
        if (!Enabled)
            return;

        // Canvas.willRenderCanvases (the Mono tick) is raised again by every
        // Canvas.ForceUpdateCanvases call, so guard against ticking twice in one frame: it would
        // redo the per-frame work and handle the same click or key press twice.
        var frame = Time.frameCount;
        if (frame == _lastTickedFrame)
            return;
        _lastTickedFrame = frame;

        try
        {
            var layoutRules = _layoutsEnabled && LayoutApplier.HasRules;
            var spriteRules = _spritesEnabled && SpriteApplier.HasRules;
            if ((layoutRules || spriteRules) && UiHooks.EnsurePatched(_harmonyId + ".Hooks", _logger, layoutRules))
            {
                // Elements enabled before the hooks existed never raised them.
                if (layoutRules)
                    LayoutApplier.ReapplyAll();
                if (spriteRules)
                    SpriteApplier.ReapplyAll();
            }

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

    /// <summary>Dumps dynamic strings (from IL) and prefab text into BepInEx/plugins/rawStrings.</summary>
    public static void DumpStrings()
    {
        if (!Directory.Exists(_rawStringsPath))
            Directory.CreateDirectory(_rawStringsPath);

        var pattern = _foreignLanguagePattern.Value;
        var dynamicCount = new StringDumperService(_logger, pattern, _managedPath, _dumpAssemblies.Value)
            .DumpFiles(_rawStringsPath);
        // Prefab text only covers objects Unity has loaded so far, which is why this is a button
        // rather than something run at startup.
        var prefabCount = new PrefabTextDumperService(_logger, pattern, _gameDataPath, _prefabTextFinder)
            .DumpAllPrefabTexts(_rawStringsPath);

        EditorWindow.SetStatus($"Dumped {dynamicCount} dynamic strings and {prefabCount} prefab texts to {_rawStringsPath}");
    }
}
