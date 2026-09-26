using BepInEx;
using FanslationStudio.Plugins.UnityShared.Editor;
using UnityEngine;

namespace FanslationStudio.Plugins.Plugins;

[BepInPlugin($"{MyPluginInfo.PLUGIN_GUID}.UIEditor", "UIEditor", MyPluginInfo.PLUGIN_VERSION)]
internal class UiEditorPlugin : BaseUnityPlugin
{
    private void Awake()
    {
        UiEditorHost.Initialize(new BepInEx5Logger(Logger), Config);
        if (!UiEditorHost.Enabled)
            return;

        // Static engine event rather than Update(): see TextResizerPlugin for why.
        Canvas.willRenderCanvases += UiEditorHost.Tick;
    }
}
