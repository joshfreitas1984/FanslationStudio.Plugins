namespace FanslationStudio.Plugins.DynamicStrings;

//Keep in sync with patch and translate
public class DynamicStringContract
{
    // The dumper writes FileName; the patcher loads DefaultFilePattern by default, so a dumped
    // file (and any split/renamed copies like dynamicStrings.part2.txt) is picked up as-is.
    public const string FileName = "dynamicStrings.txt";
    public const string DefaultFilePattern = "*dynamicStrings*";

    public string Type { get; set; } = string.Empty;

    public string Method { get; set; } = string.Empty;

    public long ILOffset { get; set; } = 0;

    public string Raw { get; set; } = string.Empty;

    public string Translation { get; set; } = string.Empty;

    public string[] Parameters { get; set; } = [];
}

public class GroupedDynamicStringContracts
{
    public string Type { get; set; } = string.Empty;

    public string Method { get; set; } = string.Empty;

    public string[] Parameters { get; set; } = [];

    public DynamicStringContract[] Contracts { get; set; } = [];
}