using BepInEx;
using BepInEx.Unity.IL2CPP;
using FanslationStudio.Plugins.SharpYaml;
using FanslationStudio.Plugins.UnityShared.Editor;
using FanslationStudio.Plugins.UnityShared.Update;

namespace FanslationStudio.Plugins.Plugins;

[BepInPlugin($"{MyPluginInfo.PLUGIN_GUID}.UIEditor", "UIEditor", MyPluginInfo.PLUGIN_VERSION)]
public class UiEditorPlugin : BasePlugin
{
    public override void Load()
    {
        UiEditorHost.Initialize(new BepInEx6Logger(base.Log), new YamlHelper(), Paths.BepInExRootPath,
            $"{MyPluginInfo.PLUGIN_GUID}.UIEditor", Paths.ManagedPath, Paths.GameDataPath, new Il2CppElementFinder());

        // Runs even when the UI Editor is disabled. Only pure .NET happens here (config, a file read and a
        // background HTTP task); the prompt UI is built later, from the tick.
        UpdateHost.Initialize(new BepInEx6Logger(base.Log), Paths.BepInExRootPath, Paths.GameRootPath);

        if (!UiEditorHost.Enabled && !UpdateHost.Enabled)
            return;

        // Per-frame tick via the shared Time.deltaTime postfix - the only tick mechanism that is
        // safe to set up from Load() under IL2CPP. See TextResizerPlugin.Load for the details.
        Il2CppFrameTick.Register($"{MyPluginInfo.PLUGIN_GUID}.UIEditor", OnTick);
    }

    private static void OnTick()
    {
        UiEditorHost.Tick();
        UpdateHost.Tick();
    }
}
