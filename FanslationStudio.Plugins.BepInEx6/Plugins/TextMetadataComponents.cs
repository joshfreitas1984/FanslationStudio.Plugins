using FanslationStudio.Plugins.TextResizer;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace FanslationStudio.Plugins.Plugins;

// Mono (BepInEx5) concrete implementations of ITextMetadata/ILegacyTextMetadata. These derive
// from Mono's UnityEngine.MonoBehaviour directly (no special constructor requirements), unlike
// the IL2CPP build, which needs its own separate concrete types (see
// FanslationStudio.Plugins.BepInEx6.IL2CPP\Plugins\TextMetadataComponents.cs).
public class TextMetadataComponent : MonoBehaviour, ITextMetadata
{
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

public class MonoBehaviourAttacher : IBehaviourAttacher
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
}
