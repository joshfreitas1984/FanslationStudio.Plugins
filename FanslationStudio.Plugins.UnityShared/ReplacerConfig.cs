using System.IO;
using BepInEx;
using BepInEx.Configuration;

namespace FanslationStudio.Plugins.UnityShared;

/// <summary>
/// Config for the Dynamic String Patcher and Prefab Text Replacer only
/// (BepInEx/config/FanslationStudio.Plugins.Replacers.cfg). It is separate from <see cref="PluginConfig"/>
/// so a game can ship it (seed-only) with the replacers already turned on.
/// </summary>
/// <remarks>
/// Both replacers must share this single instance - two ConfigFile objects on the same path would
/// overwrite each other's entries. Not listed in the UI Editor's Plugin settings view.
/// </remarks>
internal static class ReplacerConfig
{
    private static ConfigFile _file;

    public static ConfigFile File =>
        _file ??= new ConfigFile(Path.Combine(Paths.ConfigPath, $"{MyPluginInfo.PLUGIN_GUID}.Replacers.cfg"), true);
}
