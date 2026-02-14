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
            var serializer = Yaml.CreateSerializer();
            var deserializer = Yaml.CreateDeserializer();
            var folder = $"{workingDirectory}/Resizers";

            foreach (var file in Directory.EnumerateFiles(folder))
            {
                var newResizers = deserializer.Deserialize<List<TextResizerContract>>(File.ReadAllText(file));
                var content = serializer.Serialize(newResizers);
                File.WriteAllText(file, content);
            }
        }
    }
}
