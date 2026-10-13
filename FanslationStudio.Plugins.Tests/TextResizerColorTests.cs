using FanslationStudio.Plugins.TextResizer;

namespace FanslationStudio.Plugins.Tests
{
    public class TextResizerColorTests
    {
        [Theory]
        [InlineData("#FF0000", 1f, 0f, 0f, 1f)]
        [InlineData("00ff00", 0f, 1f, 0f, 1f)]
        [InlineData(" #0000FF80 ", 0f, 0f, 1f, 128 / 255f)]
        public void Parses_hex_colours(string value, float r, float g, float b, float a)
        {
            Assert.True(TextResizerService.TryParseHexColor(value, out var color));
            Assert.Equal(r, color.r, 3);
            Assert.Equal(g, color.g, 3);
            Assert.Equal(b, color.b, 3);
            Assert.Equal(a, color.a, 3);
        }

        [Theory]
        [InlineData("")]
        [InlineData("#FFF")]
        [InlineData("#GGGGGG")]
        [InlineData("#FF0000F")]
        public void Rejects_invalid_colours(string value)
        {
            Assert.False(TextResizerService.TryParseHexColor(value, out _));
        }
    }
}
