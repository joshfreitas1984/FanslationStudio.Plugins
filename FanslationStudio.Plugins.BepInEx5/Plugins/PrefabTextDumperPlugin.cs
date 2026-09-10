using BepInEx;
using FanslationStudio.Plugins.DynamicStrings;
using FanslationStudio.Plugins.PrefabText;
using FanslationStudio.Plugins.Shared;
using System.IO;
using UnityEngine;

namespace FanslationStudio.Plugins.Plugins;


// We need to wrap any services we want to use in the plugin itself,
// because BepInEx doesn't allow us to use typed references to the service in the plugin,
// and we don't want to use reflection everywhere.
public class PrefabTextDumperServiceWrapper : PrefabTextDumperService
{
    public PrefabTextDumperServiceWrapper(IPluginLogger logger, string dumpFilePath, string regexPattern, bool enabled, string gameDataPath, string bepinExPath, IPrefabTextFinder elementFinder)
        : base(logger, dumpFilePath, regexPattern, enabled, gameDataPath, bepinExPath, elementFinder)
    {
    }
}

/// <summary>
/// Used to replace hardcoded strings in IL
/// </summary>
[BepInPlugin($"{MyPluginInfo.PLUGIN_GUID}.PrefabTextDumperPlugin", "PrefabTextDumperPlugin", MyPluginInfo.PLUGIN_VERSION)]
public class PrefabTextDumperPlugin : BaseUnityPlugin
{
    public PrefabTextDumperServiceWrapper PrefabTextDumper;
    private KeyCode _dumpHotkey;

    private void Awake()
    {
        var enabled = Config.Bind("General", "Enabled", false,
            "Enable dynamic string dumping on startup").Value;
        var regexPattern = Config.Bind("General", "ForeignLanguagePattern", DynamicStringSupport.ChineseCharPattern,
            "Regex pattern for foreign language to scan for").Value;
        var dumpFiles = Config.Bind("General", "DumpFilePath", "./dumpeddata",
            "File to dump the dynamic strings to").Value;

        _dumpHotkey = Config.Bind("Hotkeys",
            "DumpHotkey",
            KeyCode.F4,
            "Dumps all prefab texts").Value;

        // BepInEx.Core 5.x's Paths class doesn't expose GameDataPath (added in later BepInEx.Core
        // versions used by the other hosts), so derive it here instead. Safe to go up one level
        // from ManagedPath under Mono - it always ends in a "Managed" subfolder of the game's
        // "*_Data" folder, unlike IL2CPP where ManagedPath points directly at "*_Data".
        var gameDataPath = Path.GetFullPath(Path.Combine(Paths.ManagedPath, ".."));
        PrefabTextDumper = new PrefabTextDumperServiceWrapper(new BepInEx5Logger(base.Logger), dumpFiles, regexPattern, enabled,
            gameDataPath, Paths.BepInExRootPath, new MonoElementFinder());
        PrefabTextDumper.Awake();
    }

    // Dumping at Awake() runs before most of the game's assets/scenes have loaded, so it tends to
    // find very little. Poll a hotkey instead so it can be triggered manually once further along.
    private void Update()
    {
        if (PrefabTextDumper == null || !PrefabTextDumperService.Enabled)
            return;

        if (UnityInput.Current.GetKeyDown(_dumpHotkey))
            PrefabTextDumper.DumpAllPrefabTexts();
    }
}
