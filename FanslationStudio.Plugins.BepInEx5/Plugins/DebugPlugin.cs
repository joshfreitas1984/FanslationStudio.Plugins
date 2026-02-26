using BepInEx;

namespace FanslationStudio.Plugins.Plugins;

[BepInPlugin($"{MyPluginInfo.PLUGIN_GUID}.DebugPlugin", "DebugPlugin", MyPluginInfo.PLUGIN_VERSION)]
internal class DebugPlugin : BaseUnityPlugin
{
    private void Awake()
    {
        Logger.LogInfo("Without this log plugins don't load");
    }
}
