using FanslationStudio.Plugins.TextResizer;
using TMPro;
using UnityEngine;

namespace FanslationStudio.Plugins.Plugins;

// Plain data holders for ITextMetadata/ILegacyTextMetadata under IL2CPP - NOT MonoBehaviours.
// These used to be custom MonoBehaviour-derived components attached via AddComponent<T>/
// GetComponent<T>, requiring ClassInjector.RegisterTypeInIl2Cpp<T>() registration. Both the
// registration call and the first-time use of the generic GetComponent<T>()/AddComponent<T>()
// interop methods have been confirmed to crash with AccessViolationException under this game's
// IL2CPP build - registration crashes even from non-Load() call sites (see
// .github/copilot-instructions.md item 2), and GetComponent<T>() crashes the first time it's
// resolved while already nested inside a native-triggered call stack (e.g. a Harmony postfix on
// Text.OnEnable, itself invoked by IL2CPP native code). Storing metadata in a plain dictionary
// keyed by GameObject.GetInstanceID() (a simple, already-resolved non-generic API) avoids both
// problems entirely. Tradeoff: entries are never removed when a GameObject is destroyed, so this
// leaks a small amount of managed memory per unique text element seen for the lifetime of the
// process - acceptable since the set of on-screen text elements is bounded and small.
public class TextMetadataComponent : ITextMetadata
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

public class LegacyTextMetadataComponent : ILegacyTextMetadata
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
