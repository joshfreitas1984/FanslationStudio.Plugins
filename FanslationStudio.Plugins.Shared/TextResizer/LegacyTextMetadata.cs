using UnityEngine;
using UnityEngine.UI;

namespace FanslationStudio.Plugins.TextResizer;

public class LegacyTextMetadata : MonoBehaviour
{
    public string ActiveResizerPath;

    public float OriginalX;
    public float OriginalY;
    public float OriginalWidth;
    public float OriginalHeight;

    public TextAnchor OriginalAlignment;
    public HorizontalWrapMode OriginalHorizontalOverflow;
    public VerticalWrapMode OriginalVerticalOverflow;

    public int OriginalFontSize;
    public float OriginalLineSpacing;
    public bool OriginalResizeTextForBestFit;
    public int OriginalResizeTextMinSize;
    public int OriginalResizeTextMaxSize;

    public float AdjustX;
    public float AdjustY;
    public float AdjustWidth;
    public float AdjustHeight;
}
