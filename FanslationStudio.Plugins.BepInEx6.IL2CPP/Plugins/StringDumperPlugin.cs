using BepInEx;
using BepInEx.Unity.IL2CPP;
using FanslationStudio.Plugins.DynamicStrings;

namespace FanslationStudio.Plugins.Plugins;

/// <summary>
/// Used to replace hardcoded strings in IL
/// </summary>
[BepInPlugin($"{MyPluginInfo.PLUGIN_GUID}.DynamicStringDumperPlugin", "DynamicStringDumperPlugin", MyPluginInfo.PLUGIN_VERSION)]
public class StringDumperPlugin : BasePlugin
{
    public StringDumperService StringDumper;

    public override void Load()
    {
        var enabled = Config.Bind("General", "Enabled", false,
            "Enable dynamic string dumping on startup").Value;
        var regexPattern = Config.Bind("General", "ForeignLanguagePattern", DynamicStringSupport.ChineseCharPattern,
            "Regex pattern for foreign language to scan for").Value;
        var dumpFiles = Config.Bind("General", "DumpFilePath", ".",
            "File to dump the dynamic strings to").Value;

        if (!enabled)
            return;

        StringDumper = new StringDumperService(new BepInEx6Logger(base.Log), dumpFiles, regexPattern, enabled, 
            Paths.ManagedPath, Paths.BepInExRootPath);
        StringDumper.Awake();
    }    
}
