using UnityEngine;

namespace FanslationStudio.Plugins.PrefabText;

// Host-runtime-specific accessor for prefab/GameObject discovery Unity API calls. Mirrors
// ISpriteElementFinder (see FanslationStudio.Plugins.Sprites) and IBehaviourAttacher (see
// FanslationStudio.Plugins.TextResizer) - Resources.FindObjectsOfTypeAll(Type) and
// GameObject.GetComponentsInChildren(Type, bool) throw MissingMethodException at runtime under
// IL2CPP when called from code compiled in Shared (against the Mono-style stub assemblies), even
// though the methods exist in the game's real assembly. See .github/copilot-instructions.md item 4.
// Must be implemented per-host.
public interface IPrefabTextFinder
{
    GameObject[] FindAllGameObjectsInResources();

    Component[] GetComponentsInChildren(GameObject gameObject, bool includeInactive);

    // AssetBundle.LoadFromFile/GetAllAssetNames/LoadAsset/Unload are non-generic Unity API calls,
    // but calling them from code compiled in Shared still throws MissingMethodException at
    // runtime under IL2CPP for the same reason as the two members above - see
    // .github/copilot-instructions.md item 4. Encapsulated as a single method here (rather than
    // exposing AssetBundle itself) so the whole load/extract/unload sequence runs in the host.
    GameObject[] LoadGameObjectsFromAssetBundle(string bundleFilePath);
}
