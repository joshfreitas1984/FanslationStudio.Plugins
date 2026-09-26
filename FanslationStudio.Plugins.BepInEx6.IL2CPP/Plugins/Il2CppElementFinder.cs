using FanslationStudio.Plugins.PrefabText;
using FanslationStudio.Plugins.TextResizer;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace FanslationStudio.Plugins.Plugins;

public class Il2CppElementFinder : IBehaviourAttacher, IPrefabTextFinder
{
    // Metadata is stored in plain dictionaries keyed by GameObject.GetInstanceID() rather than
    // attached as custom MonoBehaviour components - see TextMetadataComponents.cs for why.
    private static readonly Dictionary<int, TextMetadataComponent> _textMetadataByInstanceId = [];
    private static readonly Dictionary<int, LegacyTextMetadataComponent> _legacyTextMetadataByInstanceId = [];

    public ITextMetadata GetOrAttachTextMetadata(GameObject gameObject, out bool wasAttached)
    {
        var instanceId = gameObject.GetInstanceID();
        wasAttached = !_textMetadataByInstanceId.TryGetValue(instanceId, out var metadata);
        if (wasAttached)
        {
            metadata = new TextMetadataComponent();
            _textMetadataByInstanceId[instanceId] = metadata;
        }
        return metadata;
    }

    public ILegacyTextMetadata GetOrAttachLegacyTextMetadata(GameObject gameObject, out bool wasAttached)
    {
        var instanceId = gameObject.GetInstanceID();
        wasAttached = !_legacyTextMetadataByInstanceId.TryGetValue(instanceId, out var metadata);
        if (wasAttached)
        {
            metadata = new LegacyTextMetadataComponent();
            _legacyTextMetadataByInstanceId[instanceId] = metadata;
        }
        return metadata;
    }

    // UnityEngine.Object.FindObjectsOfType<T>() is generic, so like GetComponent<T>/
    // AddComponent<T> above, it must be called from code compiled directly in this host project
    // (against the real unhollowed assemblies) rather than from Shared, where it throws
    // MissingMethodException at runtime.
    public TextMeshProUGUI[] FindAllTextElements()
    {
        return UnityEngine.Object.FindObjectsOfType<TextMeshProUGUI>();
    }

    public Text[] FindAllLegacyTextElements()
    {
        return UnityEngine.Object.FindObjectsOfType<Text>();
    }

    // These Type-based Unity APIs must be called from code compiled directly in this host
    // project (against the real unhollowed assemblies), not from Shared, where they throw
    // MissingMethodException at runtime. The real unhollowed overloads take Il2CppSystem.Type
    // rather than System.Type, so Il2CppType.From(...) is required to convert.
    public GameObject[] FindAllGameObjectsInResources()
    {
        var objects = Resources.FindObjectsOfTypeAll(Il2CppType.From(typeof(GameObject)));
        var result = new GameObject[objects.Length];
        for (var i = 0; i < objects.Length; i++)
            result[i] = objects[i] as GameObject;
        return result;
    }

    public Component[] GetComponentsInChildren(GameObject gameObject, bool includeInactive)
    {
        var components = gameObject.GetComponentsInChildren(Il2CppType.From(typeof(Component)), includeInactive);
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
