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

    public ITextMetadata TryGetTextMetadata(GameObject gameObject)
    {
        var metadata = gameObject.GetComponent<TextMetadataComponent>();
        return metadata != null ? metadata : null;
    }

    public ILegacyTextMetadata TryGetLegacyTextMetadata(GameObject gameObject)
    {
        var metadata = gameObject.GetComponent<LegacyTextMetadataComponent>();
        return metadata != null ? metadata : null;
    }

    public TextMeshProUGUI AsTextMeshProUGUI(TMP_Text text)
    {
        return text as TextMeshProUGUI;
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
        return gameObject.GetComponentsInChildren(typeof(Component), includeInactive);
    }

    // The components with a serialized m_text/m_Text that prefab text is read from and written to.
    private static readonly System.Type[] TextComponentTypes =
        { typeof(TMP_Text), typeof(Text), typeof(TMP_InputField), typeof(InputField) };

    public Component[] FindAllTextComponentsInResources()
    {
        var result = new List<Component>();
        foreach (var type in TextComponentTypes)
        {
            foreach (var obj in Resources.FindObjectsOfTypeAll(type))
            {
                if (obj is Component component)
                    result.Add(component);
            }
        }
        return result.ToArray();
    }

    public Component[] GetTextComponentsInChildren(GameObject gameObject, bool includeInactive)
    {
        var result = new List<Component>();
        foreach (var type in TextComponentTypes)
            result.AddRange(gameObject.GetComponentsInChildren(type, includeInactive));
        return result.ToArray();
    }

    public string GetText(Component component)
    {
        if (component is TMP_Text tmp)
            return tmp.text;
        if (component is Text legacy)
            return legacy.text;
        if (component is TMP_InputField tmpInput)
            return tmpInput.text;
        if (component is InputField input)
            return input.text;
        return null;
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
            // Type-filtered, so the bundle's textures, audio etc. are never loaded just to be skipped.
            foreach (var asset in bundle.LoadAllAssets(typeof(GameObject)))
            {
                if (asset is GameObject gameObject)
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
