using BepInEx;
using BepInEx.Unity.Mono;
using FanslationStudio.Plugins.SharpYaml;
using FanslationStudio.Plugins.UnityShared.Editor;
using FanslationStudio.Plugins.UnityShared.Update;
using UnityEngine;

namespace FanslationStudio.Plugins.Plugins;

[BepInPlugin($"{MyPluginInfo.PLUGIN_GUID}.UIEditor", "UIEditor", MyPluginInfo.PLUGIN_VERSION)]
public class UiEditorPlugin : BaseUnityPlugin
{
    private void Awake()
    {
        UiEditorHost.Initialize(new BepInEx6Logger(base.Logger), new YamlHelper(), Paths.BepInExRootPath,
            $"{MyPluginInfo.PLUGIN_GUID}.UIEditor", Paths.ManagedPath, Paths.GameDataPath, new MonoElementFinder());

        // Runs even when the UI Editor is disabled. Only pure .NET happens here; the prompt UI is built from the tick.
        UpdateHost.Initialize(new BepInEx6Logger(base.Logger), Paths.BepInExRootPath, Paths.GameRootPath);

        // Static engine event rather than Update(): see TextResizerPlugin for why.
        if (UiEditorHost.Enabled)
            Canvas.willRenderCanvases += UiEditorHost.Tick;
        if (UpdateHost.Enabled)
            Canvas.willRenderCanvases += UpdateHost.Tick;
    }
}
