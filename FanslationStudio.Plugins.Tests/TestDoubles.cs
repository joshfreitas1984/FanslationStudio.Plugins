using FanslationStudio.Plugins.Shared;
using FanslationStudio.Plugins.TextResizer;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace FanslationStudio.Plugins.Tests;

// Minimal no-op IBehaviourAttacher for unit testing TextResizerService's file/save/delete logic
// without a running Unity instance. Always returns empty element arrays, so
// ApplyAllResizers()/PreviewResizer()/DeleteResizer() never actually touch any Unity API -
// they just iterate zero elements. GetOrAttachTextMetadata/GetOrAttachLegacyTextMetadata return
// plain POCOs since nothing in these tests attaches a resizer to a live on-screen element.
public class NoOpBehaviourAttacher : IBehaviourAttacher
{
    public ITextMetadata GetOrAttachTextMetadata(GameObject gameObject, out bool wasAttached)
    {
        wasAttached = true;
        return new FakeTextMetadata();
    }

    public ILegacyTextMetadata GetOrAttachLegacyTextMetadata(GameObject gameObject, out bool wasAttached)
    {
        wasAttached = true;
        return new FakeLegacyTextMetadata();
    }

    public TextMeshProUGUI[] FindAllTextElements() => [];

    public Text[] FindAllLegacyTextElements() => [];

    public Vector3[] GetWorldCorners(RectTransform rectTransform) => new Vector3[4];
}

public class FakeTextMetadata : ITextMetadata
{
    public string ActiveResizerPath { get; set; } = string.Empty;
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

public class FakeLegacyTextMetadata : ILegacyTextMetadata
{
    public string ActiveResizerPath { get; set; } = string.Empty;
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

public class NoOpLogger : IPluginLogger
{
    public void LogInfo(string message) { }
    public void LogWarning(string message) { }
    public void LogError(string message) { }
    public void LogDebug(string message) { }
    public void LogMessage(string message) { }
    public void LogFatal(string message) { }
}
