using FanslationStudio.Plugins.SharpYaml;
using FanslationStudio.Plugins.Sprites;
using FanslationStudio.Plugins.Support;

namespace FanslationStudio.Plugins.Tests;

/// <summary>
/// Sprite rules are keyed by path + original sprite, so several rules can share a path (an Image
/// the game swaps icons on, or "/*" rules that replace one sprite everywhere).
/// </summary>
public class SpriteContractRepositoryTests
{
    private static readonly YamlHelper Yaml = new();

    private static (ContractRepository<SpriteContract> repo, string folder) Create(params SpriteContract[] contracts)
    {
        var folder = Path.Combine(Path.GetTempPath(), "FSTests", nameof(SpriteContractRepositoryTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "a.yaml"), Yaml.Serialize(contracts.ToList()));

        var repo = new ContractRepository<SpriteContract>(folder, "zzAdded.yaml", Yaml, new NoOpLogger(), c => c.Path, SpriteContract.KeyOf);
        repo.Load();
        return (repo, folder);
    }

    [Fact]
    public void RulesSharingAPath_AreKeptApart_ByOriginalSprite()
    {
        var (repo, _) = Create(
            new() { Path = "/*", OriginalSprite = "icon_sword", ReplacementSprite = "sword_en" },
            new() { Path = "/*", OriginalSprite = "icon_shield", ReplacementSprite = "shield_en" });

        Assert.Equal(2, repo.Count);
        Assert.Equal("sword_en", repo.Get("/* [icon_sword]")!.ReplacementSprite);
        Assert.Equal("shield_en", repo.Get("/* [icon_shield]")!.ReplacementSprite);
    }

    [Fact]
    public void FindCandidates_ListsExactPathsFirst_ThenWildcards_InLoadOrder()
    {
        var (repo, _) = Create(
            new() { Path = "/*", OriginalSprite = "icon", ReplacementSprite = "global" },
            new() { Path = "Canvas/Bag/Slot", OriginalSprite = "icon", ReplacementSprite = "exact" },
            new() { Path = "Canvas/*/Slot", ReplacementSprite = "any" });

        var candidates = repo.FindCandidates("Canvas/Bag/Slot").Select(c => c.ReplacementSprite);

        Assert.Equal(["exact", "global", "any"], candidates);
    }

    [Fact]
    public void Save_ChangingOriginalSprite_RenamesTheKeyInPlace()
    {
        var (repo, folder) = Create(new SpriteContract { Path = "Canvas/Icon", ReplacementSprite = "a" });

        repo.Save(new SpriteContract { Path = "Canvas/Icon", OriginalSprite = "icon_a", ReplacementSprite = "a" }, previousKey: "Canvas/Icon");

        Assert.Null(repo.Get("Canvas/Icon"));
        Assert.NotNull(repo.Get("Canvas/Icon [icon_a]"));
        var saved = Yaml.Deserialize<List<SpriteContract>>(File.ReadAllText(Path.Combine(folder, "a.yaml")));
        Assert.Equal("icon_a", saved.Single().OriginalSprite);
    }

    [Fact]
    public void OldSprites2Files_StillLoad()
    {
        var folder = Path.Combine(Path.GetTempPath(), "FSTests", nameof(OldSprites2Files_StillLoad), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "zzAdded.yaml"), "- path: Canvas/Logo\n  replacementSprite: logo\n");

        var repo = new ContractRepository<SpriteContract>(folder, "zzAdded.yaml", Yaml, new NoOpLogger(), c => c.Path, SpriteContract.KeyOf);
        repo.Load();

        var rule = repo.Find("Canvas/Logo");
        Assert.Equal("logo", rule!.ReplacementSprite);
        Assert.True(rule.IsEnabled());
        Assert.Null(rule.OriginalSprite);
    }

    [Theory]
    [InlineData("icon_sword", "icon_sword")]
    [InlineData("ui/icons:sword?", "ui_icons_sword_")]
    [InlineData("", "sprite")]
    public void SafeFileName_ReplacesCharactersWindowsRejects(string name, string expected)
    {
        Assert.Equal(expected, SpriteContract.SafeFileName(name));
    }
}
