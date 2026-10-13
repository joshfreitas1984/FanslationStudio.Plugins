using FanslationStudio.Plugins.TextResizer;
using TMPro;
using UnityEngine;

namespace FanslationStudio.Plugins.Plugins;

// Mono (BepInEx5) concrete implementations of ITextMetadata/ILegacyTextMetadata. These derive
// from Mono's UnityEngine.MonoBehaviour directly (no special constructor requirements), unlike
// the IL2CPP build, which needs its own separate concrete types (see
// FanslationStudio.Plugins.BepInEx6.IL2CPP\Plugins\TextMetadataComponents.cs).
//
// Every property is [field: SerializeField] so Unity copies the captured Original*/Adjust* values
// when the game Instantiates an object that already carries this component. Plain auto-properties
// aren't serialized, so a cloned row got a metadata component full of zeros: wasAttached was false
// (nothing recaptured), and the resizer then set fontSize = 0 * percentage.
public class TextMetadataComponent : MonoBehaviour, ITextMetadata
{
    [field: SerializeField] public string ActiveResizerPath { get; set; }

    [field: SerializeField] public float OriginalX { get; set; }
    [field: SerializeField] public float OriginalY { get; set; }
    [field: SerializeField] public float OriginalWidth { get; set; }
    [field: SerializeField] public float OriginalHeight { get; set; }

    [field: SerializeField] public TextAlignmentOptions OriginalAlignment { get; set; }
    [field: SerializeField] public TextOverflowModes OriginalOverflowMode { get; set; }

    [field: SerializeField] public bool OriginalAllowWordWrap { get; set; }
    [field: SerializeField] public bool OriginalAllowAutoSizing { get; set; }

    [field: SerializeField] public float OriginalFontSize { get; set; }
    [field: SerializeField] public float OriginalLineSpacing { get; set; }
    [field: SerializeField] public float OriginalCharacterSpacing { get; set; }
    [field: SerializeField] public float OriginalWordSpacing { get; set; }

    [field: SerializeField] public float AdjustX { get; set; }
    [field: SerializeField] public float AdjustY { get; set; }
    [field: SerializeField] public float AdjustWidth { get; set; }
    [field: SerializeField] public float AdjustHeight { get; set; }

    [field: SerializeField] public Color OriginalColor { get; set; }
    [field: SerializeField] public bool ColorOverridden { get; set; }
}

public class LegacyTextMetadataComponent : MonoBehaviour, ILegacyTextMetadata
{
    [field: SerializeField] public string ActiveResizerPath { get; set; }

    [field: SerializeField] public float OriginalX { get; set; }
    [field: SerializeField] public float OriginalY { get; set; }
    [field: SerializeField] public float OriginalWidth { get; set; }
    [field: SerializeField] public float OriginalHeight { get; set; }

    [field: SerializeField] public TextAnchor OriginalAlignment { get; set; }
    [field: SerializeField] public HorizontalWrapMode OriginalHorizontalOverflow { get; set; }
    [field: SerializeField] public VerticalWrapMode OriginalVerticalOverflow { get; set; }

    [field: SerializeField] public int OriginalFontSize { get; set; }
    [field: SerializeField] public float OriginalLineSpacing { get; set; }
    [field: SerializeField] public bool OriginalResizeTextForBestFit { get; set; }
    [field: SerializeField] public int OriginalResizeTextMinSize { get; set; }
    [field: SerializeField] public int OriginalResizeTextMaxSize { get; set; }

    [field: SerializeField] public float AdjustX { get; set; }
    [field: SerializeField] public float AdjustY { get; set; }
    [field: SerializeField] public float AdjustWidth { get; set; }
    [field: SerializeField] public float AdjustHeight { get; set; }

    [field: SerializeField] public Color OriginalColor { get; set; }
    [field: SerializeField] public bool ColorOverridden { get; set; }
}
