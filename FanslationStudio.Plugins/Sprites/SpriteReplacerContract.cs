using FanslationStudio.Plugins.TextResizer;
using YamlDotNet.Core;
using YamlDotNet.Serialization;

namespace FanslationStudio.Plugins.Sprites;

public record SpriteReplacerContract
{
    [YamlMember(ScalarStyle = ScalarStyle.DoubleQuoted)]
    public string Path = string.Empty;

    [YamlMember(ScalarStyle = ScalarStyle.DoubleQuoted)]
    public string ReplacementSprite = string.Empty;

    public TextResizerContract ShallowClone()
    {
        return (TextResizerContract)MemberwiseClone();
    }
}
