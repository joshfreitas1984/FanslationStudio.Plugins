# FanslationStudio Unity Plugins

# Contacting us

You can join us here: [Discord](https://discord.gg/sqXd5ceBWT)

# Plugins

## Custom Text Resizer

Pressing Keypad_- you will add the resizer under your current cursor to `BepInEx/resizers/zzAddedResizers.yaml` you can then tweak the properties on the resizer to your liking and reload them.

Pressing Keypad_+ will reload your resizers if something looks screwy.

You can use Keypad_* to add all text items on screen. Be warned it will grab a lot!

You can use * inside the path to indicate a wildcard (ie: match zero or more characters where the * is). This will help you do one resizer for lots of stuff.

Please note I include zzAddedResizers.yaml in the patch. So if  you want to keep them move them to another yaml file when your done. Please submit any resizers you think make sense!

Here are all the things you can do: (Not including it will keep the controls defaults)

```yaml
- path: "GameStart/GameUIRoot/*/FormRoot" # Gets everything that has a FormRoot in it starting with GameStart/GameUIRoot
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