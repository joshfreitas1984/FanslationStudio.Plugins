using FanslationStudio.Plugins.PrefabText;
using FanslationStudio.Plugins.TextResizer;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace FanslationStudio.Plugins.Plugins;

// Single Mono (BepInEx5) finder/attacher implementing all the host-specific interfaces used by
// Shared services (IBehaviourAttacher, IPrefabTextFinder). Mono compiles
// directly against the real UnityEngine assemblies, so these Type-based/generic Unity API calls
// are safe to make directly here - unlike Shared, which is compiled against the Mono-style stub
// assemblies. Consolidated into one class so new hooks only need to be added/reused in one place
// per host instead of duplicated across several finder classes.
public class MonoElementFinder : IBehaviourAttacher, IPrefabTextFinder
{
    public ITextMetadata GetOrAttachTextMetadata(GameObject gameObject, out bool wasAttached)
    {
        var metadata = gameObject.GetComponent<TextMetadataComponent>();
        wasAttached = metadata == null;
        if (wasAttached)
            metadata = gameObject.AddComponent<TextMetadataComponent>();
        return metadata;
    }

    public ILegacyTextMetadata GetOrAttachLegacyTextMetadata(GameObject gameObject, out bool wasAttached)
    {
        var metadata = gameObject.GetComponent<LegacyTextMetadataComponent>();
        wasAttached = metadata == null;
        if (wasAttached)
            metadata = gameObject.AddComponent<LegacyTextMetadataComponent>();
        return metadata;
    }

    public TextMeshProUGUI[] FindAllTextElements()
    {
        return UnityEngine.Object.FindObjectsOfType<TextMeshProUGUI>();
    }

    public Text[] FindAllLegacyTextElements()
    {
        return UnityEngine.Object.FindObjectsOfType<Text>();
    }

    public GameObject[] FindAllGameObjectsInResources()
    {
        var objects = Resources.FindObjectsOfTypeAll(typeof(GameObject));
        var result = new GameObject[objects.Length];
        for (var i = 0; i < objects.Length; i++)
            result[i] = objects[i] as GameObject;
        return result;
    }

    public Component[] GetComponentsInChildren(GameObject gameObject, bool includeInactive)
    {
        var components = gameObject.GetComponentsInChildren(typeof(Component), includeInactive);
        var result = new Component[components.Length];
        for (var i = 0; i < components.Length; i++)
            result[i] = components[i] as Component;
        return result;
    }

    // AssetBundle.LoadFromFile/GetAllAssetNames/LoadAsset/Unload are non-generic Unity API calls,
    // but must still be called from a host project rather than Shared (see IPrefabTextFinder).
    public GameObject[] LoadGameObjectsFromAssetBundle(string bundleFilePath)
    {
        var result = new List<GameObject>();
        var bundle = AssetBundle.LoadFromFile(bundleFilePath);
        if (bundle == null)
            return result.ToArray();

        try
        {
            foreach (var assetName in bundle.GetAllAssetNames())
            {
                if (bundle.LoadAsset(assetName) is GameObject gameObject)
                    result.Add(gameObject);
            }
        }
        finally
        {
            bundle.Unload(false);
        }

        return result.ToArray();
    }
}
