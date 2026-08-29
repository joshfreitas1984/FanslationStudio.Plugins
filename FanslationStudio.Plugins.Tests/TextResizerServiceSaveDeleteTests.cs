using FanslationStudio.Plugins.SharpYaml;
using FanslationStudio.Plugins.TextResizer;

namespace FanslationStudio.Plugins.Tests;

// TextResizerService keeps its resizer state in static dictionaries (Resizers,
// ResizerSourceFiles, etc.), so these tests can't safely run concurrently with each other in the
// same process. Disabling collection parallelization via an assembly-level attribute wasn't
// sufficient to prevent this with this project's xunit/runner combination, so each test body is
// wrapped in an explicit lock instead - a simple, runner-independent guarantee of mutual
// exclusion.
public class TextResizerServiceSaveDeleteTests
{
    private static readonly object StaticStateLock = new();

    private static (TextResizerService service, string resizerFolder, string fileA, string fileB) CreateService(string testName)
    {
        var root = Path.Combine(Path.GetTempPath(), "FSTests", testName, Guid.NewGuid().ToString("N"));
        var resizerFolder = Path.Combine(root, "resizers");
        Directory.CreateDirectory(resizerFolder);

        var yamlHelper = new YamlHelper();
        var fileA = Path.Combine(resizerFolder, "fileA.yaml");
        var fileB = Path.Combine(resizerFolder, "fileB.yaml");

        File.WriteAllText(fileA, yamlHelper.Serialize(new List<TextResizerContract>
        {
            new() { Path = "Canvas/A", SampleText = "Hello", IdealFontSize = 20 }
        }));

        File.WriteAllText(fileB, yamlHelper.Serialize(new List<TextResizerContract>
        {
            new() { Path = "Canvas/B", SampleText = "World", IdealFontSize = 24 }
        }));

        var service = new TextResizerService(new NoOpLogger(), true, root, yamlHelper, new NoOpBehaviourAttacher());
        service.LoadResizers();

        return (service, resizerFolder, fileA, fileB);
    }

    [Fact]
    public void SaveResizer_UpdatesOwningFile_LeavesOtherFileUntouched()
    {
        lock (StaticStateLock)
        {
            var (service, _, fileA, fileB) = CreateService(nameof(SaveResizer_UpdatesOwningFile_LeavesOtherFileUntouched));

            var updated = TextResizerService.Resizers["Canvas/A"].ShallowClone();
            updated.IdealFontSize = 99;
            service.SaveResizer(updated);

            var yamlHelper = new YamlHelper();
            var fileAEntries = yamlHelper.Deserialize<List<TextResizerContract>>(File.ReadAllText(fileA));
            var fileBEntries = yamlHelper.Deserialize<List<TextResizerContract>>(File.ReadAllText(fileB));

            Assert.Single(fileAEntries);
            Assert.Equal(99, fileAEntries[0].IdealFontSize);
            Assert.Single(fileBEntries);
            Assert.Equal(24, fileBEntries[0].IdealFontSize);
            Assert.Equal(99, TextResizerService.Resizers["Canvas/A"].IdealFontSize);
        }
    }

    [Fact]
    public void SaveResizer_NewPath_WritesToAddedResizersFile()
    {
        lock (StaticStateLock)
        {
            var (service, resizerFolder, fileA, _) = CreateService(nameof(SaveResizer_NewPath_WritesToAddedResizersFile));

            var newContract = new TextResizerContract { Path = "Canvas/New", SampleText = "New", IdealFontSize = 30 };
            service.SaveResizer(newContract);

            var addedFile = Path.Combine(resizerFolder, "zzAddedResizers.yaml");
            Assert.True(File.Exists(addedFile));

            var yamlHelper = new YamlHelper();
            var addedEntries = yamlHelper.Deserialize<List<TextResizerContract>>(File.ReadAllText(addedFile));
            Assert.Single(addedEntries);
            Assert.Equal("Canvas/New", addedEntries[0].Path);

            // The pre-existing file should be completely untouched by an unrelated new resizer.
            var fileAEntries = yamlHelper.Deserialize<List<TextResizerContract>>(File.ReadAllText(fileA));
            Assert.Single(fileAEntries);
        }
    }

    [Fact]
    public void DeleteResizer_RemovesFromOwningFile_AndFromMemory()
    {
        lock (StaticStateLock)
        {
            var (service, _, fileA, fileB) = CreateService(nameof(DeleteResizer_RemovesFromOwningFile_AndFromMemory));

            service.DeleteResizer("Canvas/A");

            Assert.False(TextResizerService.Resizers.ContainsKey("Canvas/A"));

            var yamlHelper = new YamlHelper();
            var fileAContent = File.ReadAllText(fileA);
            var fileAEntries = string.IsNullOrWhiteSpace(fileAContent)
                ? new List<TextResizerContract>()
                : yamlHelper.Deserialize<List<TextResizerContract>>(fileAContent);

            Assert.Empty(fileAEntries);

            var fileBEntries = yamlHelper.Deserialize<List<TextResizerContract>>(File.ReadAllText(fileB));
            Assert.Single(fileBEntries);
        }
    }

    [Fact]
    public void GetAllResizers_ReturnsInInsertionOrder()
    {
        lock (StaticStateLock)
        {
            CreateService(nameof(GetAllResizers_ReturnsInInsertionOrder));

            var all = TextResizerService.GetAllResizers();

            Assert.Equal(2, all.Count);
            Assert.Equal("Canvas/A", all[0].Path);
            Assert.Equal("Canvas/B", all[1].Path);
        }
    }

    [Fact]
    public void PreviewResizer_DoesNotWriteToDisk()
    {
        lock (StaticStateLock)
        {
            var (service, _, fileA, _) = CreateService(nameof(PreviewResizer_DoesNotWriteToDisk));

            var before = File.ReadAllText(fileA);

            var updated = TextResizerService.Resizers["Canvas/A"].ShallowClone();
            updated.IdealFontSize = 12345;
            service.PreviewResizer(updated);

            var after = File.ReadAllText(fileA);

            Assert.Equal(before, after);
            Assert.Equal(12345, TextResizerService.Resizers["Canvas/A"].IdealFontSize);
        }
    }
}
