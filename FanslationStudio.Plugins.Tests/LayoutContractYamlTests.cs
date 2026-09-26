using FanslationStudio.Plugins.Layout;
using FanslationStudio.Plugins.Support;

namespace FanslationStudio.Plugins.Tests;

/// <summary>
/// Layout files must look the same whichever YAML backend wrote them: BepInEx5 uses YamlDotNet,
/// BepInEx6 (Mono and IL2CPP) use SharpYaml.
/// </summary>
public class LayoutContractYamlTests
{
    public static TheoryData<string> Backends => new() { "SharpYaml", "YamlDotNet" };

    private static IYamlHelper Create(string backend) => backend switch
    {
        "SharpYaml" => new FanslationStudio.Plugins.SharpYaml.YamlHelper(),
        "YamlDotNet" => new FanslationStudio.Plugins.YamlDotNet.YamlHelper(),
        _ => throw new ArgumentOutOfRangeException(nameof(backend)),
    };

    private static readonly LayoutContract Sample = new()
    {
        Path = "Canvas/AreaUIPanel/*/NameBack",
        Name = "Character card - horizontal NameBack",
        Active = true,
        AnchoredPosition = [0f, 93f],
        SizeDelta = [120f, 24f],
        LocalScale = [1f, 1f, 1f],
        RotationZ = 180f,
        CopyRectFrom = "../HeroName",
        PlaceBefore = "../HeroName",
        ImageEnabled = true,
        PreserveAspect = false,
    };

    [Theory]
    [MemberData(nameof(Backends))]
    public void Serialize_WritesVectorsInline_AndOmitsUnsetFields(string backend)
    {
        // SharpYaml writes floats as "93.0", YamlDotNet as "93"; both are fine.
        var yaml = Create(backend).Serialize(new List<LayoutContract> { Sample }).Replace(".0", string.Empty);

        Assert.Contains("anchoredPosition: [0, 93]", yaml);
        Assert.Contains("sizeDelta: [120, 24]", yaml);
        Assert.Contains("localScale: [1, 1, 1]", yaml);
        Assert.Contains("preserveAspect: false", yaml);
        Assert.DoesNotContain("anchorMin", yaml);
        Assert.DoesNotContain("enabled:", yaml);
        Assert.DoesNotContain("isEnabled", yaml, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(Backends))]
    public void RoundTrips(string backend)
    {
        var helper = Create(backend);
        var yaml = helper.Serialize(new List<LayoutContract> { Sample });

        var loaded = helper.Deserialize<List<LayoutContract>>(yaml).Single();

        Assert.Equal(Sample.Path, loaded.Path);
        Assert.Equal(Sample.Name, loaded.Name);
        Assert.Equal(Sample.AnchoredPosition, loaded.AnchoredPosition);
        Assert.Equal(Sample.LocalScale, loaded.LocalScale);
        Assert.Equal(Sample.RotationZ, loaded.RotationZ);
        Assert.Equal(Sample.PreserveAspect, loaded.PreserveAspect);
        Assert.Null(loaded.AnchorMin);
        Assert.True(loaded.IsEnabled());
    }

    [Theory]
    [MemberData(nameof(Backends))]
    public void Deserialize_AcceptsHandWrittenBlockAndFlowVectors(string backend)
    {
        const string yaml = """
            - path: Canvas/A
              enabled: false
              pivot: [0.5, 1]
              offsetPosition:
                - 5
                - -3
            """;

        var loaded = Create(backend).Deserialize<List<LayoutContract>>(yaml).Single();

        Assert.Equal([0.5f, 1f], loaded.Pivot);
        Assert.Equal([5f, -3f], loaded.OffsetPosition);
        Assert.False(loaded.IsEnabled());
    }
}
