using UnityEngine.UI;

namespace FanslationStudio.Plugins.Sprites;

// Host-runtime-specific accessor for finding Image components in the scene. Mirrors
// IBehaviourAttacher (see FanslationStudio.Plugins.TextResizer) - UnityEngine.Object.
// FindObjectsOfType<T>() is a generic Unity API call that throws MissingMethodException at
// runtime under IL2CPP when called from code compiled in Shared (against the Mono-style stub
// assemblies), even though the method exists in the game's real assembly. See
// .github/copilot-instructions.md item 4. Must be implemented per-host.
public interface ISpriteElementFinder
{
    Image[] FindAllElements();
}
