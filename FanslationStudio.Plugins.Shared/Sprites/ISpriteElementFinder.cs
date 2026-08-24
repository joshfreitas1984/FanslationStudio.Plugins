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

    // RectTransform.GetWorldCorners(Vector3[]) throws MissingMethodException at runtime under
    // IL2CPP when called from code compiled in Shared, even though the method exists in the
    // game's real assembly - see .github/copilot-instructions.md item 4. Must be implemented
    // per-host.
    UnityEngine.Vector3[] GetWorldCorners(UnityEngine.RectTransform rectTransform);

    // Reads the raw/encoded bytes of a texture for export/dumping. If the texture isn't marked
    // readable, blits it to a temporary RenderTexture and reads it back. Untested against a live
    // IL2CPP crash, but treated as suspect per the broadened item 4 guidance (Texture2D/
    // RenderTexture/Graphics.Blit calls) - must be implemented per-host, not called from Shared.
    byte[] GetExportableTextureBytes(UnityEngine.Texture2D texture);

    // Creates a replacement Sprite from encoded image bytes (e.g. a dumped/translated PNG). Same
    // reasoning as GetExportableTextureBytes above - Texture2D/Sprite.Create calls must be made
    // from a host project, not from Shared.
    UnityEngine.Sprite CreateReplacementSprite(byte[] bytes, UnityEngine.Rect rect, UnityEngine.Vector2 pivot, float pixelsPerUnit);
}
