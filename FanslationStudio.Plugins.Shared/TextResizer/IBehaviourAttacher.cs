using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace FanslationStudio.Plugins.TextResizer;

// Host-runtime-specific factory/accessor for text metadata components. Mono (BepInEx5,
// BepInEx6) and IL2CPP (BepInEx6.IL2CPP) each need their own concrete MonoBehaviour-derived
// metadata types (different base MonoBehaviour, and IL2CPP additionally requires an (IntPtr)
// constructor and ClassInjector registration), so each host project supplies its own
// implementation of this attacher and passes it into TextResizerService.
public interface IBehaviourAttacher
{
    // Returns the existing TextMetadata component on gameObject, attaching (and returning) a new
    // one if it wasn't already present. wasAttached is true only when a new component was added,
    // so the caller knows to populate its initial/original values.
    ITextMetadata GetOrAttachTextMetadata(GameObject gameObject, out bool wasAttached);

    ILegacyTextMetadata GetOrAttachLegacyTextMetadata(GameObject gameObject, out bool wasAttached);

    // UnityEngine.Object.FindObjectsOfType<T>() is a generic Unity API call. Under IL2CPP, calling
    // it from code compiled in Shared (against the Mono-style stub assemblies) throws
    // MissingMethodException at runtime even though the method exists in the game's real
    // assembly - see .github/copilot-instructions.md item 4. Must be implemented per-host.
    TextMeshProUGUI[] FindAllTextElements();

    // Same reason as FindAllTextElements above - generic FindObjectsOfType<T>() call, must be
    // implemented per-host.
    Text[] FindAllLegacyTextElements();
}

