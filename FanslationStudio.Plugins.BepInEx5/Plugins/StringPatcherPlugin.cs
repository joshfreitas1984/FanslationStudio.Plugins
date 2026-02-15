using BepInEx;
using BepInEx.Logging;
using FanslationStudio.Plugins.DynamicStrings;
using FanslationStudio.Plugins.Shared;
using FanslationStudio.Plugins.Support;
using FanslationStudio.Plugins.TextResizer;
using FanslationStudio.Plugins.YamlDotNet;
using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace FanslationStudio.Plugins.Plugins;

// We need to wrap any services we want to use in the plugin itself,
// because BepInEx doesn't allow us to use typed references to the service in the plugin,
// and we don't want to use reflection everywhere.
public class StringPatcherServiceWrapper : StringPatcherService
{
    public StringPatcherServiceWrapper(IPluginLogger logger, bool enabled, Harmony harmony, string bepinExRootPath, IYamlHelper yamlHelper)
        : base(logger, enabled, harmony, bepinExRootPath, yamlHelper)
    {
    }
}

/// <summary>
/// Used to replace hardcoded strings in IL
/// </summary>
[BepInPlugin($"{MyPluginInfo.PLUGIN_GUID}.DynamicStringPatcher", "DynamicStringPatcher", MyPluginInfo.PLUGIN_VERSION)]
public class StringPatcherPlugin : BaseUnityPlugin
{
    public static bool _enabled = true;
    public StringPatcherServiceWrapper StringPatcherService;

    private void Awake()
    {
        _enabled = Config.Bind("General",
            "Enabled",
            false,
            "Turn plugin to replace dyanmic strings that are hardcoded in code").Value;

        StringPatcherService = new StringPatcherServiceWrapper(new BepInEx5Logger(base.Logger),
            _enabled,
            new Harmony($"{MyPluginInfo.PLUGIN_GUID}.DynamicStringPatcher"),
            Paths.BepInExRootPath,
            new YamlHelper());

        StringPatcherService.Awake();
    }
}