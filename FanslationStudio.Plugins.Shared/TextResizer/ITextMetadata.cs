using TMPro;

namespace FanslationStudio.Plugins.TextResizer;

// Data-only abstraction over the TextMetadata component. TextMetadata itself must be a
// MonoBehaviour so it can live on a GameObject, but the concrete MonoBehaviour base type
// differs between Mono (BepInEx5) and IL2CPP (BepInEx6.IL2CPP) builds. Each host project
// provides its own concrete component implementing this interface so the shared
// TextResizerService never needs to know which runtime it's running under.
public interface ITextMetadata
{
    string ActiveResizerPath { get; set; }

    float OriginalX { get; set; }
    float OriginalY { get; set; }
    float OriginalWidth { get; set; }
    float OriginalHeight { get; set; }

    TextAlignmentOptions OriginalAlignment { get; set; }
    TextOverflowModes OriginalOverflowMode { get; set; }

    bool OriginalAllowWordWrap { get; set; }
    bool OriginalAllowAutoSizing { get; set; }

    float OriginalFontSize { get; set; }
    float OriginalLineSpacing { get; set; }
    float OriginalCharacterSpacing { get; set; }
    float OriginalWordSpacing { get; set; }

    float AdjustX { get; set; }
    float AdjustY { get; set; }
    float AdjustWidth { get; set; }
    float AdjustHeight { get; set; }
}
