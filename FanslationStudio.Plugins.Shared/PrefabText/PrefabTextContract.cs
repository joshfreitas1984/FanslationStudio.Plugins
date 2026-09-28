namespace FanslationStudio.Plugins.PrefabText;

// One entry of the translated prefabText.txt, e.g.
//   - raw: 开始游戏
//     result: Start Game
// Newlines are written as a literal "\n" (matching PrefabTextDumperService's output).
public class PrefabTextContract
{
    // The dumper writes FileName; the replacer loads DefaultFilePattern by default, so a dumped
    // file (and any split/renamed copies like prefabText.menus.txt) is picked up as-is.
    public const string FileName = "prefabText.txt";
    public const string DefaultFilePattern = "*prefabText*";

    public string Raw { get; set; } = string.Empty;

    public string Result { get; set; } = string.Empty;
}
