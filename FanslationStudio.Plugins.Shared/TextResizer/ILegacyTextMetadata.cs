using UnityEngine;

namespace FanslationStudio.Plugins.TextResizer;

// See ITextMetadata for why this is an interface rather than a concrete MonoBehaviour.
public interface ILegacyTextMetadata
{
    string ActiveResizerPath { get; set; }

    float OriginalX { get; set; }
    float OriginalY { get; set; }
    float OriginalWidth { get; set; }
    float OriginalHeight { get; set; }

    TextAnchor OriginalAlignment { get; set; }
    HorizontalWrapMode OriginalHorizontalOverflow { get; set; }
    VerticalWrapMode OriginalVerticalOverflow { get; set; }

    int OriginalFontSize { get; set; }
    float OriginalLineSpacing { get; set; }
    bool OriginalResizeTextForBestFit { get; set; }
    int OriginalResizeTextMinSize { get; set; }
    int OriginalResizeTextMaxSize { get; set; }

    float AdjustX { get; set; }
    float AdjustY { get; set; }
    float AdjustWidth { get; set; }
    float AdjustHeight { get; set; }
}
