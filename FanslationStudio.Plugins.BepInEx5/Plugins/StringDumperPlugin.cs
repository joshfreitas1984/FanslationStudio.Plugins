using BepInEx;
using FanslationStudio.Plugins.DynamicStrings;
using FanslationStudio.Plugins.Shared;

namespace FanslationStudio.Plugins.Plugins;


// We need to wrap any services we want to use in the plugin itself,
// because BepInEx doesn't allow us to use typed references to the service in the plugin,
// and we don't want to use reflection everywhere.
public class StringDumperServiceWrapper : StringDumperService
{
    public StringDumperServiceWrapper(IPluginLogger logger, string dumpFilePath, string regexPattern, bool enabled, string managedPath, string bepinExPath)
        : base(logger, dumpFilePath, regexPattern, enabled, managedPath, bepinExPath)
    {
    }
}

/// <summary>
/// Used to replace hardcoded strings in IL
/// </summary>
[BepInPlugin($"{MyPluginInfo.PLUGIN_GUID}.DynamicStringDumperPlugin", "DynamicStringDumperPlugin", MyPluginInfo.PLUGIN_VERSION)]
public class StringDumperPlugin : BaseUnityPlugin
{
    public StringDumperServiceWrapper StringDumper;

    private void Awake()
    {
        var enabled = Config.Bind("General", "Enabled", false,
            "Enable dynamic string dumping on startup").Value;
        var regexPattern = Config.Bind("General", "ForeignLanguagePattern", DynamicStringSupport.ChineseCharPattern,
            "Regex pattern for foreign language to scan for").Value;
        var dumpFiles = Config.Bind("General", "DumpFilePath", "./dumpeddata",
            "File to dump the dynamic strings to").Value;

        StringDumper = new StringDumperServiceWrapper(new BepInEx5Logger(base.Logger), dumpFiles, regexPattern, enabled, 
            Paths.ManagedPath, Paths.BepInExRootPath);
        StringDumper.Awake();
    }
}
