using System.IO;
using BepInEx;
using BepInEx.Configuration;

namespace FanslationStudio.Plugins.UnityShared;

/// <summary>
/// The one config file shared by every plugin in this pack: the UI Editor's
/// (BepInEx/config/FanslationStudio.Plugins.UIEditor.cfg), so all settings live in one place and
/// can be edited from the editor window's Plugin settings view.
/// </summary>
/// <remarks>
/// Plugins bind to this instead of their own <c>Config</c>. It's created on first use, so it
/// doesn't matter which plugin the chainloader loads first. Every plugin must share this single
/// instance - two ConfigFile objects on the same path would overwrite each other's entries.
/// </remarks>
internal static class PluginConfig
{
    private static ConfigFile _file;

    public static ConfigFile File =>
        _file ??= new ConfigFile(Path.Combine(Paths.ConfigPath, $"{MyPluginInfo.PLUGIN_GUID}.UIEditor.cfg"), true);
}
