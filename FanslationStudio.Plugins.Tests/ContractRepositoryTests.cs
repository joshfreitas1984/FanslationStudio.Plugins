using FanslationStudio.Plugins.Layout;
using FanslationStudio.Plugins.SharpYaml;
using FanslationStudio.Plugins.Support;

namespace FanslationStudio.Plugins.Tests;

public class ContractRepositoryTests
{
    private static readonly YamlHelper Yaml = new();

    private static (ContractRepository<LayoutContract> repo, string folder) Create(params (string file, LayoutContract[] contracts)[] files)
    {
        var folder = Path.Combine(Path.GetTempPath(), "FSTests", nameof(ContractRepositoryTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        foreach (var (file, contracts) in files)
            File.WriteAllText(Path.Combine(folder, file), Yaml.Serialize(contracts.ToList()));

        var repo = new ContractRepository<LayoutContract>(folder, "zzAddedLayouts.yaml", Yaml, new NoOpLogger(), c => c.Path);
        repo.Load();
        return (repo, folder);
    }

    private static List<LayoutContract> Read(string folder, string file)
    {
        var content = File.ReadAllText(Path.Combine(folder, file));
        return string.IsNullOrWhiteSpace(content) ? [] : Yaml.Deserialize<List<LayoutContract>>(content);
    }

    [Fact]
    public void Load_FilesInAlphabeticalOrder_FirstPathWins()
    {
        var (repo, _) = Create(
            ("b.yaml", [new() { Path = "Canvas/A", RotationZ = 2 }]),
            ("a.yaml", [new() { Path = "Canvas/A", RotationZ = 1 }, new() { Path = "Canvas/B" }]));

        Assert.Equal(2, repo.Count);
        Assert.Equal(1f, repo.Get("Canvas/A")!.RotationZ);
        Assert.EndsWith("a.yaml", repo.GetSourceFile("Canvas/A"));
    }

    [Fact]
    public void Find_PrefersExactMatch_ThenFirstWildcardInLoadOrder()
    {
        var (repo, _) = Create(
            ("a.yaml", [new() { Path = "Canvas/*/Name", RotationZ = 1 }]),
            ("b.yaml", [new() { Path = "Canvas/Card/Name", RotationZ = 2 }, new() { Path = "/*", RotationZ = 3 }]));

        Assert.Equal(2f, repo.Find("Canvas/Card/Name")!.RotationZ);
        Assert.Equal(1f, repo.Find("Canvas/Other/Name")!.RotationZ);
        Assert.Equal(3f, repo.Find("Somewhere/Else")!.RotationZ);
    }

    [Fact]
    public void Find_ReturnsNullWhenNothingMatches()
    {
        var (repo, _) = Create(("a.yaml", [new() { Path = "Canvas/*/Name" }]));
        Assert.Null(repo.Find("Canvas/Name/Child"));
    }

    [Fact]
    public void Save_New_GoesToDefaultFile()
    {
        var (repo, folder) = Create(("a.yaml", [new() { Path = "Canvas/A" }]));

        var file = repo.Save(new LayoutContract { Path = "Canvas/New", RotationZ = 90 });

        Assert.EndsWith("zzAddedLayouts.yaml", file);
        Assert.Single(Read(folder, "zzAddedLayouts.yaml"));
        Assert.Single(Read(folder, "a.yaml"));
    }

    [Fact]
    public void Save_Existing_RewritesOnlyOwningFile()
    {
        var (repo, folder) = Create(
            ("a.yaml", [new() { Path = "Canvas/A" }]),
            ("b.yaml", [new() { Path = "Canvas/B" }]));
        var bBefore = File.ReadAllText(Path.Combine(folder, "b.yaml"));

        repo.Save(new LayoutContract { Path = "Canvas/A", RotationZ = 45 });

        Assert.Equal(45f, Read(folder, "a.yaml").Single().RotationZ);
        Assert.Equal(bBefore, File.ReadAllText(Path.Combine(folder, "b.yaml")));
        Assert.False(File.Exists(Path.Combine(folder, "zzAddedLayouts.yaml")));
    }

    [Fact]
    public void Save_Rename_ReplacesEntryInPlace_InSameFile()
    {
        var (repo, folder) = Create(("a.yaml",
        [
            new() { Path = "Canvas/First" },
            new() { Path = "Canvas/Card1/Name" },
            new() { Path = "Canvas/Last" },
        ]));

        repo.Save(new LayoutContract { Path = "Canvas/*/Name" }, previousKey: "Canvas/Card1/Name");

        Assert.Equal(["Canvas/First", "Canvas/*/Name", "Canvas/Last"], Read(folder, "a.yaml").Select(c => c.Path));
        Assert.Null(repo.Get("Canvas/Card1/Name"));
        Assert.Equal(3, repo.Count);
    }

    [Fact]
    public void Delete_RemovesFromOwningFile_AndKeepsOrderForLaterAdds()
    {
        var (repo, folder) = Create(("a.yaml",
        [
            new() { Path = "Canvas/1" },
            new() { Path = "Canvas/2" },
            new() { Path = "Canvas/3" },
        ]));

        Assert.True(repo.Delete("Canvas/2"));
        Assert.False(repo.Delete("Canvas/Missing"));
        repo.Preview(new LayoutContract { Path = "Canvas/4" });

        Assert.Equal(["Canvas/1", "Canvas/3"], Read(folder, "a.yaml").Select(c => c.Path));
        Assert.Equal(["Canvas/1", "Canvas/3", "Canvas/4"], repo.All.Select(c => c.Path));
    }

    [Fact]
    public void Preview_IsNotPersisted_UntilSaved()
    {
        var (repo, folder) = Create(("a.yaml", [new() { Path = "Canvas/A", RotationZ = 1 }]));
        var versionBefore = repo.Version;

        repo.Preview(new LayoutContract { Path = "Canvas/A", RotationZ = 99 });

        Assert.Equal(99f, repo.Find("Canvas/A")!.RotationZ);
        Assert.Equal(1f, Read(folder, "a.yaml").Single().RotationZ);
        Assert.True(repo.Version > versionBefore);

        repo.Load();
        Assert.Equal(1f, repo.Find("Canvas/A")!.RotationZ);
    }

    [Fact]
    public void DiscardPreview_RemovesPreviewOnlyEntries_NeverSavedOnes()
    {
        var (repo, folder) = Create(("a.yaml", [new() { Path = "Canvas/Saved" }]));
        repo.Preview(new LayoutContract { Path = "Canvas/Draft" });
        repo.Preview(new LayoutContract { Path = "Canvas/Saved", RotationZ = 5 });

        Assert.True(repo.DiscardPreview("Canvas/Draft"));
        Assert.False(repo.DiscardPreview("Canvas/Saved"));
        Assert.False(repo.DiscardPreview("Canvas/Missing"));

        Assert.Null(repo.Find("Canvas/Draft"));
        Assert.NotNull(repo.Find("Canvas/Saved"));
        Assert.Single(Read(folder, "a.yaml"));
    }

    [Fact]
    public void Preview_InvalidatesWildcardCache()
    {
        var (repo, _) = Create(("a.yaml", [new() { Path = "Canvas/A" }]));
        Assert.Null(repo.Find("Canvas/Card/Name"));

        repo.Preview(new LayoutContract { Path = "Canvas/*/Name" });

        Assert.NotNull(repo.Find("Canvas/Card/Name"));
    }

    [Fact]
    public void Load_SkipsBrokenFiles_AndEntriesWithoutPath()
    {
        var (repo, folder) = Create(("a.yaml", [new() { Path = "Canvas/A" }, new() { Path = "" }]));
        File.WriteAllText(Path.Combine(folder, "b.yaml"), "- path: [unclosed");

        repo.Load();

        Assert.Equal(["Canvas/A"], repo.All.Select(c => c.Path));
    }

    [Fact]
    public void FindCandidates_ExactFirst_ThenWildcardsInLoadOrder()
    {
        var (repo, _) = Create(
            ("a.yaml", [new() { Path = "/Name", RotationZ = 1 }, new() { Path = "Canvas/Card/Name", RotationZ = 2 }]),
            ("b.yaml", [new() { Path = "Canvas/*", RotationZ = 3 }]));

        Assert.Equal([2f, 1f, 3f], repo.FindCandidates("Canvas/Card/Name").Select(c => c.RotationZ));
        Assert.Empty(repo.FindCandidates("Other/Card/Title"));
    }

    [Fact]
    public void CouldMatchName_UsesLeafNames_UnlessAnyLeafIsWildcard()
    {
        var (repo, _) = Create(("a.yaml", [new() { Path = "Canvas/*/Name" }, new() { Path = "/Title/Text" }]));

        Assert.True(repo.CouldMatchName("Name"));
        Assert.True(repo.CouldMatchName("Text"));
        Assert.False(repo.CouldMatchName("Title"));
        // "Canvas/Card/Item/Name" can also be an object named "Item/Name" under Canvas/Card.
        Assert.True(repo.CouldMatchName("Item/Name"));

        repo.Preview(new LayoutContract { Path = "Canvas/List/*" });
        Assert.True(repo.CouldMatchName("Anything"));

        repo.DiscardPreview("Canvas/List/*");
        Assert.False(repo.CouldMatchName("Anything"));
    }

    [Fact]
    public void Index_TracksSaveAndDelete()
    {
        var (repo, _) = Create(("a.yaml", [new() { Path = "Canvas/A" }]));
        Assert.Null(repo.Find("Canvas/B"));

        repo.Save(new LayoutContract { Path = "Canvas/B" });
        Assert.NotNull(repo.Find("Canvas/B"));
        Assert.True(repo.CouldMatchName("B"));

        repo.Delete("Canvas/B");
        Assert.Null(repo.Find("Canvas/B"));
        Assert.False(repo.CouldMatchName("B"));
    }
}
