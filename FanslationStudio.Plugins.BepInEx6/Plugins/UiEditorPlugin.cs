using BepInEx;
using BepInEx.Unity.Mono;
using FanslationStudio.Plugins.SharpYaml;
using FanslationStudio.Plugins.UnityShared.Editor;
using UnityEngine;

namespace FanslationStudio.Plugins.Plugins;

[BepInPlugin($"{MyPluginInfo.PLUGIN_GUID}.UIEditor", "UIEditor", MyPluginInfo.PLUGIN_VERSION)]
public class UiEditorPlugin : BaseUnityPlugin
{
    private void Awake()
    {
        UiEditorHost.Initialize(new BepInEx6Logger(base.Logger), Config, new YamlHelper(), Paths.BepInExRootPath,
            $"{MyPluginInfo.PLUGIN_GUID}.UIEditor");
        if (!UiEditorHost.Enabled)
            return;

        // Static engine event rather than Update(): see TextResizerPlugin for why.
        Canvas.willRenderCanvases += UiEditorHost.Tick;
    }
}
