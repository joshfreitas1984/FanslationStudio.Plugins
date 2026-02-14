using BepInEx;
using BepInEx.Logging;
using FanslationStudio.Plugins.DynamicStrings;
using FanslationStudio.Plugins.Support;
using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace FanslationStudio.Plugins.Plugins;

/// <summary>
/// Used to replace hardcoded strings in IL
/// </summary>
[BepInPlugin($"{MyPluginInfo.PLUGIN_GUID}.DynamicStringPatcher", "DynamicStringPatcher", MyPluginInfo.PLUGIN_VERSION)]
public class StringPatcherPlugin : BaseUnityPlugin
{
    public static bool _enabled = true;
    public StringPatcherService StringPatcherService;

    private void Awake()
    {
        _enabled = Config.Bind("General",
            "Enabled",
            false,
            "Turn plugin to replace dyanmic strings that are hardcoded in code").Value;

        StringPatcherService = new StringPatcherService(new BepInEx5Logger(base.Logger), 
            _enabled,
            new Harmony($"{MyPluginInfo.PLUGIN_GUID}.DynamicStringPatcher"),
            Paths.BepInExRootPath);

        StringPatcherService.Awake();
    }
}