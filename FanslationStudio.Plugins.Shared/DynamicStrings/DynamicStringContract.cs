namespace FanslationStudio.Plugins.DynamicStrings;

//Keep in sync with patch and translate
public class DynamicStringContract
{
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