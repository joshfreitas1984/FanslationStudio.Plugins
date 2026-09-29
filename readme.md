# FanslationStudio Unity Plugins

# Contacting us

You can join us here: [Discord](https://discord.gg/sqXd5ceBWT)

# Plugins

## Configuration

Every plugin's settings live in one file, `BepInEx/config/FanslationStudio.Plugins.UIEditor.cfg`. You can also change them in game: the UI Editor window's **Plugin settings** button (title bar) lists every setting grouped by plugin, and saves as you type. Most changes take effect after restarting the game.

| Section | Settings |
| --- | --- |
| `[General]` | `Enabled` - the UI Editor itself |
| `[Layouts]`, `[Sprites]` | `Enabled` |
| `[Editor]` | `WindowScale`, `OpenOnPick`, `AutoSave` |
| `[Hotkeys]` | See Hotkeys below |
| `[Dumping]` | `ForeignLanguagePattern`, `Assemblies` (default `Assembly-CSharp*.dll`) |
| `[Updates]` | `Enabled`, `PromptDelaySeconds` (default 15) |
| `[TextResizer]` | `Enabled` |
| `[DynamicStringPatcher]` | `Enabled`, `ResourcePath`, `FilePattern` (default `*dynamicStrings*`) |
| `[PrefabTextReplacer]` | `Enabled`, `ResourcePath`, `FilePattern` (default `*prefabText*`) |

> **Changed:** TextResizer, DynamicStringPatcher and PrefabTextReplacer used to have their own `.cfg` files. Those are no longer read - copy any values you changed into the sections above (`TextResizerEnabled` is now `[TextResizer] Enabled`).

## Update prompt

When the game starts the plugin checks GitHub for a newer patch release and, if one exists, shows a small "Update available" window (**Update now** / **Later**). Update now downloads the patch and the latest installer to a temp folder, closes the game, and the installer applies the update and restarts the game through Steam. If the check or download fails, the log says why and nothing else changes.

There is nothing to configure per game. The repo, Steam app ID and zip name are read from `BepInEx/release-manifest.json`, which the installer writes when it applies a patch built by `FanslationStudio.LlmKit.Release`. A manual install without that manifest never prompts. Set `[Updates] Enabled = false` to turn it off. Wired into the BepInEx 5, BepInEx 6 Mono and BepInEx 6 IL2CPP hosts (Mono hosts tick from `Canvas.willRenderCanvases`, and force TLS 1.2 for old Unity runtimes).

## UI Editor

An in-game editor for text resizers, layouts (position/size/anchors/visibility) and sprite replacements. Rules are saved as YAML under `BepInEx/`, one folder per kind, all keyed by the element's hierarchy path:

| Folder | What it holds | New rules go to |
| --- | --- | --- |
| `resizers/` | Text resizers (font size, spacing, wrapping, ...) | `zzAddedResizers.yaml` |
| `layouts/` | Layout rules (RectTransform, active state, image/layout toggles) | `zzAddedLayouts.yaml` |
| `sprites2/` | Sprite replacements; images live in `sprites2/dumped/` | `zzAdded.yaml` |

### Hotkeys

Defaults, changeable under `[Hotkeys]` in the config or in **Plugin settings** (the config only takes a new default when the key isn't already in the file):

| Action | Default |
| --- | --- |
| Pick the element under the cursor | Alt + Middle Click |
| Pick a tooltip or popup: press once, hover to open it, press again to pick whatever appeared or changed | Alt + 2 (or Shift + Alt + 2 for games that need Shift held) |
| Cycle through the elements under the cursor | Alt + Mouse Wheel (or Page Up / Page Down) |
| Go to parent / child | `[` / `]` |
| Clear the selection | Alt + Mouse Back |
| Show / hide the editor window | Alt + 1 |
| Reload layouts, sprites and resizers from disk | Alt + Mouse Forward |

Picking highlights the element (pink tint, outline and a status bar at the top) and opens the editor window. Hotkeys are ignored while you type in a text box.

### The editor window

- **Left column:** *Under cursor* lists every element stacked under the pick point. *Rules* lists every existing rule of every kind, with a badge (**L** layout, **T** text, **S** sprite) and a search box. Opening a rule selects its element if it's on screen, otherwise edits it detached.
- **Tabs:** Layout, Text and Sprite. Changes preview live. Save writes to the file that owns the rule (new rules go to the `zzAdded*` file), Discard reverts, Delete removes it.
- **Auto-save:** changed rules are saved when you move to another element, switch tab or close the window (`Editor/AutoSave`).
- Other settings: `Editor/WindowScale` (e.g. 1.5 on 4K) and `Editor/OpenOnPick`. Layouts and sprites can each be turned off with `Layouts/Enabled` and `Sprites/Enabled`.

### Layouts

```yaml
- path: "Canvas/Panel/Title"   # Wildcards as described under Path matching rules
  name: "Move the title"       # Optional description
  enabled: true                # false keeps the rule without applying it
  enforce: false               # Re-apply every frame, for elements the game keeps moving
  active: true                 # Show/hide the GameObject
  anchorMin: [0.5, 1]
  anchorMax: [0.5, 1]
  pivot: [0.5, 0.5]
  anchoredPosition: [0, -40]   # Absolute position...
  offsetPosition: [10, 0]      # ...or an offset from the game's (or the absolute) value
  sizeDelta: [300, 60]         # Absolute size...
  offsetSize: [40, 0]          # ...or an offset
  localPosition: [0, 0, 0]
  localScale: [1, 1, 1]
  rotationZ: 0
  copyRectFrom: "../HeroName"  # Copy anchors/pivot/position from another element (applied first)
  copySizeFromSource: false    # Also copy its size
  placeBefore: "../HeroName"   # Draw immediately before this sibling
  imageEnabled: true
  preserveAspect: true
  contentSizeFitterEnabled: false
  layoutGroupEnabled: false    # Horizontal/Vertical/Grid layout group
```

Every field is optional; anything left out keeps the game's value. Values are always calculated from the element's original values, so re-applying never drifts, and removing a field (or the rule) restores the original.

### Sprites

```yaml
- path: "Canvas/Title/Logo"         # The Image's path; use "/*" with originalSprite to replace a sprite everywhere
  replacementSprite: "title_logo"   # sprites2/dumped/title_logo.png
  originalSprite: "title_logo"      # Only replace while the Image shows this sprite (leave out to replace whatever it shows)
  name: "English logo"
  enabled: true
```

Workflow in the Sprite tab: **Dump original** (writes just that sprite, not its whole atlas, to `sprites2/dumped/`; existing files are never overwritten unless you use **Overwrite dump**), edit the PNG (**Open folder**), then **Reload PNG** to see it immediately. **Dump all visible sprites** dumps everything on screen. A replacement drawn at a different resolution (e.g. 2x) is shown at the original's size, with the same pivot and 9-slice border. Deleting a rule keeps its PNG.

> **Changed:** the separate SpriteReplacer plugin (and its hotkeys) has been removed; existing `sprites2` YAML files still load.

## Custom Text Resizer

Resizers are created and edited in the UI Editor's Text tab, or by hand in `BepInEx/resizers/*.yaml`. The old TextResizer hotkeys (Keypad / * +) have been removed; use Alt + Mouse Forward to reload.

You can use * inside the path to indicate a wildcard (ie: match zero or more characters where the * is, including `/`). This will help you do one resizer for lots of stuff.

Path matching rules (shared by resizers, layouts and sprites):

- The pattern must match the **whole** path. `Canvas/*/Title` will not match `Canvas/Panel/Title/Child`; add a trailing `*` (`Canvas/*/Title*`) if you want children too.
- A leading `/` means "at any depth". `/Title/Text` matches any path ending in `Title/Text`, and `/*` matches everything (use it for a global resizer in a file that sorts last, e.g. `zzzGlobalResizer.yaml`).
- Every other character is literal, so `[UI]`, `Text (TMP)` and `.` need no escaping.
- When several wildcard entries match, the first one loaded wins (files load alphabetically).

> **Changed:** older versions matched wildcard patterns anywhere inside the path, so `A/*/B` also matched `X/A/1/B/C`. If a wildcard resizer stops applying after updating, add a leading `/` or a trailing `*`.

Please note I include zzAddedResizers.yaml in the patch. So if  you want to keep them move them to another yaml file when your done. Please submit any resizers you think make sense!

Here are all the things you can do: (Not including it will keep the controls defaults)

```yaml
- path: "GameStart/GameUIRoot/*/FormRoot" # Any FormRoot at any depth under GameStart/GameUIRoot
  sampleText: "Commission"    # Dumped text so you know what the path was for
  idealFontSize: 30           # The font size you want
  allowWordWrap: false        # Allows word wrapping on component
  allowAutoSizing: false      # Lets the font change sizes depending on width given by dev
  allowLeftTrimText: true     # Allow the text to be left trimmed
  adjustX: 0                  # Positive or negative number to adjust left and right 
  adjustY: 0                  # Positive or negative number to adjust up and down
  adjustWidth: 0              # Positive or negative number to adjust allowed width of control
  adjustHeight: 0             # Positive or negative number to adjust allowed height of control
  minFontSize: 0              # Min Font size when autosizing
  maxFontSize: 0              # Max Font size when autosizing
  lineSpacing: 0.0            # Line spacing for text
  characterSpacing: 0.0       # Character spacing for text
  wordSpacing: 0.0            # Word spacing for text
  fontPercentage: 0.70        # Percentage of font size to use (replaces max/min font size if set above 0)
  alignment: Center           # Control alignment on screen for TextMeshProGUI
  overflow: Overflow          # Overflow mode for TextMeshProGUI
```

## String Dumping

The UI Editor window's **Plugin settings** view (title bar button) has a **Dump strings** button. It writes both dumps to `BepInEx/plugins/rawStrings/` (created if missing):

- `dynamicStrings.txt` - hardcoded strings found in the game's compiled assemblies. Only `Assembly-CSharp*.dll` is scanned by default. If the game keeps its code in other DLLs in `<Game>_Data/Managed`, list them under `[Dumping] Assemblies`, separated by `;` (e.g. `Assembly-CSharp.dll;Mortal.*.dll`). The patcher finds those types without extra setup.
- `prefabText.txt` - text baked into prefabs. This only sees objects the game has loaded, so press it once you're past the menus/scenes you care about.

Only strings matching `ForeignLanguagePattern` (under `[Dumping]` in the UI Editor config, CJK characters by default) are included. The `[Dumping]` settings are read each time you press the button, so you don't need to restart after changing them.

## Prefab Text Replacer

Replaces text baked into prefabs and scenes (TextMeshPro and legacy UGUI `Text`) using translated prefab text files. Mono games only (BepInEx 5 and BepInEx 6 Mono) - not available for IL2CPP.

Set `Enabled = true` under `[PrefabTextReplacer]` in the config, then put the translated files in `BepInEx/<ResourcePath>/` (`ResourcePath` defaults to `./english`). Every file matching `FilePattern` (default `*prefabText*`, ignoring case, e.g. `prefabText.txt`, `menus.PrefabText.txt`) is loaded alphabetically; if two files translate the same text, the later one wins. The Dynamic String Patcher works the same way with `*dynamicStrings*`:

```yaml
- raw: 开始游戏
  result: Start Game
- raw: 第一行\n第二行      # Newlines are written as \n, exactly as dumped
  result: First line\nSecond line
```

Text is replaced as assets are loaded (`Resources.Load`, asset bundles, including async loads) and in everything already loaded whenever a scene loads.
