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

    public ITextMetadata TryGetTextMetadata(GameObject gameObject)
    {
        return _textMetadataByInstanceId.TryGetValue(gameObject.GetInstanceID(), out var metadata) ? metadata : null;
    }

    public ILegacyTextMetadata TryGetLegacyTextMetadata(GameObject gameObject)
    {
        return _legacyTextMetadataByInstanceId.TryGetValue(gameObject.GetInstanceID(), out var metadata) ? metadata : null;
    }

    // TryCast, not `as`: the Harmony postfix receives a wrapper typed as TMP_Text.
    public TextMeshProUGUI AsTextMeshProUGUI(TMP_Text text)
    {
        return text == null ? null : text.TryCast<TextMeshProUGUI>();
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

    // The components with a serialized m_text/m_Text that prefab text is read from.
    private static readonly System.Type[] TextComponentTypes =
        [typeof(TMP_Text), typeof(Text), typeof(TMP_InputField), typeof(InputField)];

    public Component[] FindAllTextComponentsInResources()
    {
        var result = new List<Component>();
        foreach (var type in TextComponentTypes)
            AddComponents(Resources.FindObjectsOfTypeAll(Il2CppType.From(type)), result);
        return result.ToArray();
    }

    public Component[] GetTextComponentsInChildren(GameObject gameObject, bool includeInactive)
    {
        var result = new List<Component>();
        foreach (var type in TextComponentTypes)
        {
            foreach (var component in gameObject.GetComponentsInChildren(Il2CppType.From(type), includeInactive))
                AddComponent(component, result);
        }
        return result.ToArray();
    }

    private static void AddComponents(Il2CppReferenceArray<UnityEngine.Object> objects, List<Component> result)
    {
        foreach (var obj in objects)
            AddComponent(obj, result);
    }

    // TryCast, not `as`: interop wrappers come back typed as the array's declared element type.
    private static void AddComponent(UnityEngine.Object obj, List<Component> result)
    {
        var component = obj == null ? null : obj.TryCast<Component>();
        if (component != null)
            result.Add(component);
    }

    // TryCast, not `as`/`is`: the wrappers come back typed as Component.
    public string GetText(Component component)
    {
        var tmp = component.TryCast<TMP_Text>();
        if (tmp != null)
            return tmp.text;

        var legacy = component.TryCast<Text>();
        if (legacy != null)
            return legacy.text;

        var tmpInput = component.TryCast<TMP_InputField>();
        if (tmpInput != null)
            return tmpInput.text;

        var input = component.TryCast<InputField>();
        return input != null ? input.text : null;
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
            foreach (var asset in bundle.LoadAllAssets(Il2CppType.From(typeof(GameObject))))
            {
                var gameObject = asset == null ? null : asset.TryCast<GameObject>();
                if (gameObject != null)
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
