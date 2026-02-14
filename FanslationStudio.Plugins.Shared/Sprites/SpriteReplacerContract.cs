using FanslationStudio.Plugins.TextResizer;

namespace FanslationStudio.Plugins.Sprites;

public record SpriteReplacerContract
{
    public string Path = string.Empty;

    public string ReplacementSprite = string.Empty;

    public TextResizerContract ShallowClone()
    {
        return (TextResizerContract)MemberwiseClone();
    }
}
