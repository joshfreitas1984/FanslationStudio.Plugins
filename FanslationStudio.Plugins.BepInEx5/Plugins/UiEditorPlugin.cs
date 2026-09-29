using BepInEx;
using FanslationStudio.Plugins.YamlDotNet;
using FanslationStudio.Plugins.UnityShared.Editor;
using FanslationStudio.Plugins.UnityShared.Update;
using System.IO;
using UnityEngine;

namespace FanslationStudio.Plugins.Plugins;

[BepInPlugin($"{MyPluginInfo.PLUGIN_GUID}.UIEditor", "UIEditor", MyPluginInfo.PLUGIN_VERSION)]
internal class UiEditorPlugin : BaseUnityPlugin
{
    private void Awake()
    {
        // BepInEx.Core 5.x's Paths class doesn't expose GameDataPath (added in later BepInEx.Core
        // versions used by the other hosts), so derive it here instead. Safe to go up one level
        // from ManagedPath under Mono - it always ends in a "Managed" subfolder of the game's
        // "*_Data" folder, unlike IL2CPP where ManagedPath points directly at "*_Data".
        var gameDataPath = Path.GetFullPath(Path.Combine(Paths.ManagedPath, ".."));
        UiEditorHost.Initialize(new BepInEx5Logger(Logger), new YamlHelper(), Paths.BepInExRootPath,
            $"{MyPluginInfo.PLUGIN_GUID}.UIEditor", Paths.ManagedPath, gameDataPath, new MonoElementFinder());

        // Runs even when the UI Editor is disabled. Only pure .NET happens here; the prompt UI is built from the tick.
        UpdateHost.Initialize(new BepInEx5Logger(Logger), Paths.BepInExRootPath, Paths.GameRootPath);

        // Static engine event rather than Update(): see TextResizerPlugin for why.
        if (UiEditorHost.Enabled)
            Canvas.willRenderCanvases += UiEditorHost.Tick;
        if (UpdateHost.Enabled)
            Canvas.willRenderCanvases += UpdateHost.Tick;
    }
}
