using FanslationStudio.Plugins.DynamicStrings;
using FanslationStudio.Plugins.PrefabText;
using FanslationStudio.Plugins.Support;

namespace FanslationStudio.Plugins.Tests
{
    public class TranslationFilesTests
    {
        [Theory]
        // The dumpers' own output always matches the defaults
        [InlineData(DynamicStringContract.FileName, DynamicStringContract.DefaultFilePattern, true)]
        [InlineData(PrefabTextContract.FileName, PrefabTextContract.DefaultFilePattern, true)]
        // Anything before/after the name, any case
        [InlineData("menus.dynamicStrings.part2.txt", "*dynamicStrings*", true)]
        [InlineData("DYNAMICSTRINGS.TXT", "*dynamicStrings*", true)]
        [InlineData("dumpedPrefabText.txt", "*prefabText*", true)]
        [InlineData("prefabtext.menus.yaml", "*prefabText*", true)]
        // Other files in the same folder are left alone
        [InlineData("prefabText.txt", "*dynamicStrings*", false)]
        [InlineData("strings.txt", "*dynamicStrings*", false)]
        // ? is one character; other characters are literal
        [InlineData("prefabText1.txt", "prefabText?.txt", true)]
        [InlineData("prefabText.txt", "prefabText?.txt", false)]
        [InlineData("prefabTextXtxt", "prefabText.txt", false)]
        public void IsMatch(string fileName, string pattern, bool expected)
        {
            Assert.Equal(expected, TranslationFiles.IsMatch(fileName, pattern));
        }

        [Fact]
        public void Find_ReturnsMatchesInAlphabeticalOrder_IgnoringCase()
        {
            var folder = Path.Combine(Path.GetTempPath(), "TranslationFilesTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                foreach (var name in new[] { "b.PrefabText.txt", "a.prefabtext.txt", "dynamicStrings.txt" })
                    File.WriteAllText(Path.Combine(folder, name), string.Empty);

                var found = TranslationFiles.Find(folder, "*prefabText*").Select(Path.GetFileName);

                Assert.Equal(new[] { "a.prefabtext.txt", "b.PrefabText.txt" }, found);
            }
            finally
            {
                Directory.Delete(folder, true);
            }
        }

        [Fact]
        public void Find_MissingFolder_ReturnsEmpty()
        {
            Assert.Empty(TranslationFiles.Find(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")), "*"));
        }
    }
}
