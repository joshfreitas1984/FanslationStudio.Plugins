using System;
using FanslationStudio.Plugins.TextResizer;
using Il2CppInterop.Runtime.Injection;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace FanslationStudio.Plugins.Plugins;

// IL2CPP concrete implementations of ITextMetadata/ILegacyTextMetadata. These must derive from
// the IL2CPP-interop MonoBehaviour and declare the (IntPtr) constructor that Il2CppInterop uses
// to wrap native instances, and must be registered with ClassInjector before use. Using the
// Mono-compiled equivalents from FanslationStudio.Plugins.Shared here would corrupt IL2CPP's
// type/method tables (surfacing later as an unrelated AccessViolationException).
public class TextMetadataComponent : MonoBehaviour, ITextMetadata
{
    public TextMetadataComponent(IntPtr ptr) : base(ptr) { }

    public string ActiveResizerPath { get; set; }

    public float OriginalX { get; set; }
    public float OriginalY { get; set; }
    public float OriginalWidth { get; set; }
    public float OriginalHeight { get; set; }

    public TextAlignmentOptions OriginalAlignment { get; set; }
    public TextOverflowModes OriginalOverflowMode { get; set; }

    public bool OriginalAllowWordWrap { get; set; }
    public bool OriginalAllowAutoSizing { get; set; }

    public float OriginalFontSize { get; set; }
    public float OriginalLineSpacing { get; set; }
    public float OriginalCharacterSpacing { get; set; }
    public float OriginalWordSpacing { get; set; }

    public float AdjustX { get; set; }
    public float AdjustY { get; set; }
    public float AdjustWidth { get; set; }
    public float AdjustHeight { get; set; }
}

public class LegacyTextMetadataComponent : MonoBehaviour, ILegacyTextMetadata
{
    public LegacyTextMetadataComponent(IntPtr ptr) : base(ptr) { }

    public string ActiveResizerPath { get; set; }

    public float OriginalX { get; set; }
    public float OriginalY { get; set; }
    public float OriginalWidth { get; set; }
    public float OriginalHeight { get; set; }

    public TextAnchor OriginalAlignment { get; set; }
    public HorizontalWrapMode OriginalHorizontalOverflow { get; set; }
    public VerticalWrapMode OriginalVerticalOverflow { get; set; }

    public int OriginalFontSize { get; set; }
    public float OriginalLineSpacing { get; set; }
    public bool OriginalResizeTextForBestFit { get; set; }
    public int OriginalResizeTextMinSize { get; set; }
    public int OriginalResizeTextMaxSize { get; set; }

    public float AdjustX { get; set; }
    public float AdjustY { get; set; }
    public float AdjustWidth { get; set; }
    public float AdjustHeight { get; set; }
}

public class Il2CppBehaviourAttacher : IBehaviourAttacher
{
    public static void EnsureRegistered()
    {
        //if (!ClassInjector.IsTypeRegisteredInIl2Cpp<TextMetadataComponent>())
        //    ClassInjector.RegisterTypeInIl2Cpp<TextMetadataComponent>();

        //if (!ClassInjector.IsTypeRegisteredInIl2Cpp<LegacyTextMetadataComponent>())
        //    ClassInjector.RegisterTypeInIl2Cpp<LegacyTextMetadataComponent>();
    }

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
}
