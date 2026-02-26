using BepInEx;
using BepInEx.Unity.Mono;
using FanslationStudio.Plugins.DynamicStrings;

namespace FanslationStudio.Plugins.Plugins;

/// <summary>
/// Used to replace hardcoded strings in IL
/// </summary>
[BepInPlugin($"{MyPluginInfo.PLUGIN_GUID}.DynamicStringDumperPlugin", "DynamicStringDumperPlugin", MyPluginInfo.PLUGIN_VERSION)]
public class StringDumperPlugin : BaseUnityPlugin
{
    public StringDumperService StringDumper;

    private void Awake()
    {
        var enabled = Config.Bind("General", "Enabled", false,
            "Enable dynamic string dumping on startup").Value;
        var regexPattern = Config.Bind("General", "ForeignLanguagePattern", DynamicStringSupport.ChineseCharPattern,
            "Regex pattern for foreign language to scan for").Value;
        var dumpFiles = Config.Bind("General", "DumpFilePath", ".",
            "File to dump the dynamic strings to").Value;

        StringDumper = new StringDumperService(new BepInEx6Logger(base.Logger), dumpFiles, regexPattern, enabled, 
            Paths.ManagedPath, Paths.BepInExRootPath);
        StringDumper.Awake();
    }    
}
