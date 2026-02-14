using TMPro;
using UnityEngine;

namespace FanslationStudio.Plugins.TextResizer;

public class TextMetadata : MonoBehaviour
{
    public string ActiveResizerPath;

    public float OriginalX;
    public float OriginalY;
    public float OriginalWidth;
    public float OriginalHeight;

    public TextAlignmentOptions OriginalAlignment;
    public TextOverflowModes OriginalOverflowMode;

    public bool OriginalAllowWordWrap;
    public bool OriginalAllowAutoSizing;

    public float OriginalFontSize;
    public float OriginalLineSpacing;
    public float OriginalCharacterSpacing;
    public float OriginalWordSpacing;

    public float AdjustX;
    public float AdjustY;
    public float AdjustWidth;
    public float AdjustHeight;
}