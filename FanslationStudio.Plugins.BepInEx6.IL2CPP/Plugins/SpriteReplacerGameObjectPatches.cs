using HarmonyLib;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.UI;

namespace FanslationStudio.Plugins.Plugins;

// GameObject.SetActive postfix that needs to walk a hierarchy for Image components. Previously
// lived in SpriteReplacerService.cs (in FanslationStudio.Plugins.Shared) and threw
// MissingMethodException at runtime under IL2CPP, even though the method genuinely exists in the
// game's real assembly - Shared is compiled against the Mono-style stub assemblies in Reference\,
// which are physically different from the game's real unhollowed assemblies, and Il2CppInterop
// can't resolve calls made from IL compiled against the stub. This class must stay in the
// BepInEx6.IL2CPP host project, compiled directly against unhollowed\, for its Unity API calls
// to resolve correctly. See .github/copilot-instructions.md item 4.
public static class SpriteReplacerGameObjectPatches
{
    private static bool _patched;

    /// <summary>
    /// Applies these patches. Must be called after at least one frame/scene has run (e.g. from
    /// the plugin's Update, not from Load()) for the same reason as SpriteReplacerService.EnsurePatched.
    /// </summary>
    public static void EnsurePatched()
    {
        if (_patched)
            return;

        Harmony.CreateAndPatchAll(typeof(SpriteReplacerGameObjectPatches), $"{MyPluginInfo.PLUGIN_GUID}.SpriteReplacer.GameObjectPatches");
        _patched = true;
    }

    [HarmonyPostfix, HarmonyPatch(typeof(GameObject), nameof(GameObject.SetActive), [typeof(bool)])]
    public static void Postfix_GameObject_SetActive(GameObject __instance)
    {
        if (!FanslationStudio.Plugins.Sprites.SpriteReplacerService.ContractsLoaded)
            return;

        var items = __instance.GetComponentsInChildren(Il2CppType.From(typeof(Image)), false);
        foreach (var item in items)
        {
            if (item is Image image)
                FanslationStudio.Plugins.Sprites.SpriteReplacerService.ReplaceSpriteInAsset(image);
        }
    }
}
