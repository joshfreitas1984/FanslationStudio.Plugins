# FanslationStudio Unity Plugins

# Contacting us

You can join us here: [Discord](https://discord.gg/sqXd5ceBWT)

# Plugins

## UI Editor

An in-game editor for text resizers, layouts (position/size/anchors/visibility) and sprite replacements. Rules are saved as YAML under `BepInEx/`, one folder per kind, all keyed by the element's hierarchy path:

| Folder | What it holds | New rules go to |
| --- | --- | --- |
| `resizers/` | Text resizers (font size, spacing, wrapping, ...) | `zzAddedResizers.yaml` |
| `layouts/` | Layout rules (RectTransform, active state, image/layout toggles) | `zzAddedLayouts.yaml` |
| `sprites2/` | Sprite replacements; images live in `sprites2/dumped/` | `zzAdded.yaml` |

### Hotkeys

Defaults, changeable in `BepInEx/config/FanslationStudio.Plugins.UIEditor.cfg` (the config only takes a new default when the key isn't already in the file):

| Action | Default |
| --- | --- |
| Pick the element under the cursor | Alt + Mouse Forward |
| Cycle through the elements under the cursor | Alt + Mouse Wheel (or Page Up / Page Down) |
| Go to parent / child | `[` / `]` |
| Clear the selection | Alt + 1 |
| Show / hide the editor window | Alt + Mouse Back |
| Reload layouts, sprites and resizers from disk | Alt + Middle Click |

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

Resizers are created and edited in the UI Editor's Text tab, or by hand in `BepInEx/resizers/*.yaml`. The old TextResizer hotkeys (Keypad / * +) have been removed; use Alt + Middle Click to reload.

You can use * inside the path to indicate a wildcard (ie: match zero or more characters where the * is, including `/`). This will help you do one resizer for lots of stuff.

Path matching rules (shared by resizers, layouts and sprites):

- The pattern must match the **whole** path. `Canvas/*/Title` will not match `Canvas/Panel/Title/Child`; add a trailing `*` (`Canvas/*/Title*`) if you want children too.
- A leading `/` means "at any depth". `/Title/Text` matches any path ending in `Title/Text`, and `/*` matches everything (use it for a global resizer in a file that sorts last, e.g. `zzz.GlobalResizer.yaml`).
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

## Dynamic String Dumper

This will dump any dynamic strings found in the compiled assembly that matches the regex pattern set in the plugin config. To turn on switch the enabled flag to true and update the file path to where you want the file to goto.

## Prefab Text Dumper

This will dump any strings hard coded into prefabs that matches the regex pattern set in the plugin config. To turn on switch the enabled flag to true and update the file path to where you want the file to goto.