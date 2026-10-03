using System;
using UnityEngine;

namespace FanslationStudio.Plugins.UnityShared.Sprites;

/// <summary>
/// Texture work for sprite dumping and replacement. Lives in UnityShared (compiled per host) so
/// Texture2D/RenderTexture/Sprite.Create calls are made against the host's real assemblies.
/// ImageConversion is called statically: the IL2CPP interop has no extension-method forms.
/// </summary>
internal static class SpriteImages
{
    /// <summary>
    /// Encodes just this sprite's pixels as PNG - not the whole texture, which for atlased
    /// sprites would be the entire atlas. Works for non-readable textures by blitting them to a
    /// temporary RenderTexture first.
    ///
    /// The crop happens in the Blit (UV scale/offset into a sprite-sized RenderTexture) and the
    /// whole RenderTexture is then read from (0,0). Reading a sub-rect with ReadPixels instead
    /// depends on the graphics API's y origin, which wrapped the image on some IL2CPP games.
    /// </summary>
    public static byte[] EncodeSpriteToPng(Sprite sprite)
    {
        var texture = sprite.texture;
        if (texture == null || texture.width <= 0 || texture.height <= 0)
            return null;

        var region = GetTextureRegion(sprite);
        var width = Mathf.Max(1, Mathf.RoundToInt(region.width));
        var height = Mathf.Max(1, Mathf.RoundToInt(region.height));
        var scale = new Vector2(region.width / texture.width, region.height / texture.height);
        var offset = new Vector2(region.x / texture.width, region.y / texture.height);

        var renderTexture = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default);
        var previous = RenderTexture.active;
        Texture2D readable = null;
        try
        {
            Graphics.Blit(texture, renderTexture, scale, offset);
            RenderTexture.active = renderTexture;
            readable = new Texture2D(width, height, TextureFormat.RGBA32, false);
            readable.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            readable.Apply();
            return ImageConversion.EncodeToPNG(readable);
        }
        finally
        {
            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(renderTexture);
            if (readable != null)
                UnityEngine.Object.Destroy(readable);
        }
    }

    /// <summary>
    /// Loads a PNG as a texture. Marked DontUnloadUnusedAsset so Resources.UnloadUnusedAssets
    /// (which games call on scene changes) doesn't free it while it's cached.
    ///
    /// Kept readable: uGUI's Image.IsRaycastLocationValid reads the sprite's pixels when a game
    /// uses alphaHitTestMinimumThreshold (shaped buttons), and logs an error on every raycast if
    /// the texture is non-readable.
    /// </summary>
    public static Texture2D LoadTexture(byte[] png, string name, FilterMode filterMode)
    {
        // Uncompressed: LoadImage replaces the size, and compressed formats need specific sizes.
        var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        if (!ImageConversion.LoadImage(texture, png, false))
        {
            UnityEngine.Object.Destroy(texture);
            return null;
        }

        texture.name = name;
        texture.filterMode = filterMode;
        texture.wrapMode = TextureWrapMode.Clamp;
        texture.hideFlags = HideFlags.DontUnloadUnusedAsset;
        return texture;
    }

    /// <summary>
    /// Builds a replacement that behaves like the original: same pivot, same 9-slice border and
    /// the same on-screen size even if the PNG was drawn at a different resolution (e.g. 2x).
    /// </summary>
    public static Sprite CreateReplacement(Texture2D texture, Sprite original)
    {
        var originalRect = original.rect;
        var scale = originalRect.width > 0 ? texture.width / originalRect.width : 1f;

        // Sprite.pivot is in pixels; Sprite.Create wants it normalised (0-1).
        var pivot = new Vector2(
            originalRect.width > 0 ? original.pivot.x / originalRect.width : 0.5f,
            originalRect.height > 0 ? original.pivot.y / originalRect.height : 0.5f);

        var sprite = Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), pivot,
            original.pixelsPerUnit * scale, 0, SpriteMeshType.FullRect, original.border * scale);
        sprite.name = original.name;
        sprite.hideFlags = HideFlags.DontUnloadUnusedAsset;
        return sprite;
    }

    private static Rect GetTextureRegion(Sprite sprite)
    {
        try
        {
            // Where the sprite sits in its (possibly atlas) texture.
            return sprite.textureRect;
        }
        catch (Exception)
        {
            // Tightly packed atlas sprites have no rectangular region; fall back to the rect.
            return sprite.rect;
        }
    }
}
