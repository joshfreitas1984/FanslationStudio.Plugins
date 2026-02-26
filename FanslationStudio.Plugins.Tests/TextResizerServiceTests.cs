using FanslationStudio.Plugins.SharpYaml;
using FanslationStudio.Plugins.Support;
using FanslationStudio.Plugins.TextResizer;

namespace FanslationStudio.Plugins.Tests
{
    public class TextResizerServiceTests
    {
        const string workingDirectory = "../../..";

        [Fact]
        public void ReserializeResizerTest()
        {
            var helper = new YamlHelper();

            var folder = $"{workingDirectory}/Resizers";

            foreach (var file in Directory.EnumerateFiles(folder))
            {
                var newResizers = helper.Deserialize<List<TextResizerContract>>(File.ReadAllText(file));
                var content = helper.Serialize(newResizers);
                File.WriteAllText(file, content);
            }
        }
    }
}
