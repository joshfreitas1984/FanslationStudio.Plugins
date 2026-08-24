using FanslationStudio.Plugins.PrefabText;
using FanslationStudio.Plugins.Sprites;
using FanslationStudio.Plugins.TextResizer;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace FanslationStudio.Plugins.Plugins;

public class Il2CppElementFinder : IBehaviourAttacher, ISpriteElementFinder, IPrefabTextFinder
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

    public UnityEngine.UI.Image[] FindAllElements()
    {
        return UnityEngine.Object.FindObjectsOfType<UnityEngine.UI.Image>();
    }

    // RectTransform.GetWorldCorners(Vector3[]) must be called from code compiled directly in
    // this host project (against the real unhollowed assemblies), not from Shared, where it
    // throws MissingMethodException at runtime - see .github/copilot-instructions.md item 4.
    //
    // CONFIRMED BUG (fixed here): the real unhollowed overload takes
    // Il2CppStructArray<Vector3>, not a plain Vector3[]. Passing a Vector3[] compiles fine
    // (there's an implicit Vector3[] -> Il2CppStructArray<Vector3> conversion), but that
    // conversion allocates a brand-new native array and copies the (empty) input into it - the
    // native GetWorldCorners call then writes its results into that temporary copy, not into
    // the original managed array, so the caller always read back four zeroed Vector3s. Fix:
    // allocate the Il2CppStructArray<Vector3> directly and convert the result back to a managed
    // array afterwards (Il2CppArrayBase<T> has an implicit operator to T[] that copies out).
    public Vector3[] GetWorldCorners(RectTransform rectTransform)
    {
        var corners = new Il2CppStructArray<Vector3>(4);
        rectTransform.GetWorldCorners(corners);
        return corners;
    }

    // Texture2D/RenderTexture/Graphics.Blit/Sprite.Create calls must be made from a host project
    // rather than Shared - see ISpriteElementFinder.
    public byte[] GetExportableTextureBytes(Texture2D texture)
    {
        if (texture.isReadable)
            return texture.GetRawTextureData();

        var readableTexture = new Texture2D(texture.width, texture.height, texture.format, false);
        var renderTexture = RenderTexture.GetTemporary(texture.width, texture.height);

        Graphics.Blit(texture, renderTexture);
        RenderTexture.active = renderTexture;
        readableTexture.ReadPixels(new Rect(0, 0, renderTexture.width, renderTexture.height), 0, 0);
        readableTexture.Apply();

        RenderTexture.active = null;
        RenderTexture.ReleaseTemporary(renderTexture);

        // The unhollowed ImageConversionModule doesn't expose EncodeToPNG/LoadImage as instance/
        // extension methods with optional parameters like Mono does - call the static
        // ImageConversion methods directly instead.
        var bytes = ImageConversion.EncodeToPNG(readableTexture);
        UnityEngine.Object.Destroy(readableTexture);
        return bytes;
    }

    public Sprite CreateReplacementSprite(byte[] bytes, Rect rect, Vector2 pivot, float pixelsPerUnit)
    {
        // We use an uncompressed format to avoid issues with compression requiring specific sizes (eg. DXT1, DXT5, BC7, BC6H)
        // Texture size doesn't matter, will be replaced by Unity in LoadImage to match texture
        var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        ImageConversion.LoadImage(texture, bytes, false);

        if (rect.width > texture.width || rect.height > texture.height)
        {
            rect.width = Mathf.Min(rect.width, texture.width);
            rect.height = Mathf.Min(rect.height, texture.height);
        }

        return Sprite.Create(texture, rect, pivot, pixelsPerUnit);
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
