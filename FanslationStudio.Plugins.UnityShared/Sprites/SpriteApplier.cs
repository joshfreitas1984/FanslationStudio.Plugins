using System;
using System.Collections.Generic;
using System.IO;
using FanslationStudio.Plugins.Shared;
using FanslationStudio.Plugins.Sprites;
using FanslationStudio.Plugins.Support;
using FanslationStudio.Plugins.UnityShared.Editor;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace FanslationStudio.Plugins.UnityShared.Sprites;

/// <summary>
/// Replaces Image sprites according to <see cref="SpriteContract"/> rules.
///
///   * Applied when an Image is enabled, when the game sets Image.sprite (see <see cref="UiHooks"/>),
///     on scene change and on reload.
///   * Replacement sprites keep the original's pivot, 9-slice border and on-screen size.
///     Textures and sprites are cached; our own sprites are tracked so they're never replaced again.
///   * The original sprite is remembered per Image, so a rule can be reverted - but only if the
///     Image still shows our replacement (if the game has moved on, its sprite is left alone).
/// </summary>
internal static class SpriteApplier
{
    private sealed class Replaced
    {
        public int Id;
        public Image Image;
        public Sprite Original;
        public Sprite Replacement;
        public string Path;
        public string File;
    }

    private static ContractRepository<SpriteContract> _repository;
    private static IPluginLogger _logger;

    private static readonly Dictionary<int, Replaced> _replaced = new Dictionary<int, Replaced>();
    private static readonly Dictionary<string, Texture2D> _textures = new Dictionary<string, Texture2D>();
    private static readonly Dictionary<string, Sprite> _sprites = new Dictionary<string, Sprite>();
    private static readonly HashSet<int> _ourSprites = new HashSet<int>();
    // Image.sprite is set often (sprite animation); don't rebuild hierarchy paths every time.
    private static readonly Dictionary<int, string> _pathCache = new Dictionary<int, string>();
    private static readonly HashSet<string> _warned = new HashSet<string>();

    private static bool _applying;
    private static int _lastSceneHandle;

    public static ContractRepository<SpriteContract> Repository => _repository;
    public static bool HasRules => _repository != null && _repository.Count > 0;
    public static string DumpFolder => Path.Combine(_repository.Folder, "dumped");

    public static void Initialize(ContractRepository<SpriteContract> repository, IPluginLogger logger)
    {
        _repository = repository;
        _logger = logger;
        _repository.Load();
        Directory.CreateDirectory(DumpFolder);
        _lastSceneHandle = SceneManager.GetActiveScene().handle;

        UiHooks.GraphicEnabled += graphic =>
        {
            if (!HasRules)
                return;
            var image = UiCompat.As<Image>(graphic);
            if (image != null)
                Apply(image, CachedPath(image));
        };
        UiHooks.ImageSpriteSet += image =>
        {
            if (HasRules)
                Apply(image, CachedPath(image));
        };

        _logger.LogInfo($"[UIEditor] Loaded {_repository.Count} sprite rule(s) from '{_repository.Folder}'.");
    }

    public static void Tick()
    {
        if (_repository == null)
            return;

        var sceneHandle = SceneManager.GetActiveScene().handle;
        if (sceneHandle == _lastSceneHandle)
            return;

        _lastSceneHandle = sceneHandle;
        _pathCache.Clear();
        PruneDestroyed();
        ReapplyAll();
    }

    public static string PngPath(string fileName) => Path.Combine(DumpFolder, fileName + ".png");

    public static bool IsOurs(Sprite sprite) => sprite != null && _ourSprites.Contains(sprite.GetInstanceID());

    /// <summary>The sprite the game put on the Image (not our replacement).</summary>
    public static Sprite GetOriginalSprite(Image image)
    {
        if (image == null)
            return null;
        if (_replaced.TryGetValue(image.GetInstanceID(), out var entry) && image.sprite == entry.Replacement)
            return entry.Original;
        return image.sprite;
    }

    /// <summary>The rule that applies to this Image right now, if any.</summary>
    public static SpriteContract Match(string path, string spriteName)
    {
        if (_repository == null)
            return null;

        foreach (var contract in _repository.FindCandidates(path))
        {
            if (!contract.IsEnabled())
                continue;
            if (string.IsNullOrEmpty(contract.OriginalSprite) || contract.OriginalSprite == spriteName)
                return contract;
        }
        return null;
    }

    public static void Apply(Image image, string path = null)
    {
        if (_applying || image == null)
            return;

        var current = image.sprite;
        if (current == null || IsOurs(current))
            return;

        path = path ?? ObjectHelper.GetGameObjectPath(image.gameObject);
        if (path.StartsWith(ElementPicker.EditorObjectPrefix))
            return;

        var contract = Match(path, current.name);
        if (contract == null)
            return;

        var replacement = GetReplacement(contract.ReplacementSprite, current);
        if (replacement == null)
            return;

        var id = image.GetInstanceID();
        _replaced[id] = new Replaced
        {
            Id = id, Image = image, Original = current, Replacement = replacement, Path = path, File = contract.ReplacementSprite,
        };

        _applying = true;
        try
        {
            image.sprite = replacement;
        }
        finally
        {
            _applying = false;
        }
    }

    public static void Revert(Image image)
    {
        if (image != null && _replaced.TryGetValue(image.GetInstanceID(), out var entry))
            Revert(entry);
    }

    /// <summary>Reverts every replaced Image, then applies the current rules to every Image.</summary>
    public static void ReapplyAll()
    {
        foreach (var entry in new List<Replaced>(_replaced.Values))
            Revert(entry);

        if (!HasRules)
            return;

        foreach (var image in UiCompat.FindObjectsOfType<Image>())
            Apply(image);
    }

    /// <summary>Reloads rules and PNGs from disk and re-applies everything.</summary>
    public static void Reload()
    {
        foreach (var entry in new List<Replaced>(_replaced.Values))
            Revert(entry);
        ClearCaches(null);
        _repository.Load();
        ReapplyAll();
        _logger.LogInfo($"[UIEditor] Reloaded {_repository.Count} sprite rule(s).");
    }

    /// <summary>Re-applies rules to Images affected by a change to the given path patterns.</summary>
    public static void Refresh(IList<string> patterns)
    {
        bool Matches(string path)
        {
            foreach (var pattern in patterns)
            {
                if (!string.IsNullOrEmpty(pattern) && PathPattern.IsMatch(pattern, path))
                    return true;
            }
            return false;
        }

        foreach (var entry in new List<Replaced>(_replaced.Values))
        {
            if (Matches(entry.Path))
                Revert(entry);
        }

        foreach (var image in UiCompat.FindObjectsOfType<Image>())
        {
            if (image == null)
                continue;
            var path = ObjectHelper.GetGameObjectPath(image.gameObject);
            if (Matches(path))
                Apply(image, path);
        }
    }

    /// <summary>Re-reads one PNG from disk (after editing it) and re-applies it.</summary>
    public static void ReloadFile(string fileName)
    {
        var affected = new List<Image>();
        foreach (var entry in new List<Replaced>(_replaced.Values))
        {
            if (entry.File == fileName)
            {
                affected.Add(entry.Image);
                Revert(entry);
            }
        }

        ClearCaches(fileName);
        _warned.Remove("missing:" + fileName);

        foreach (var image in affected)
            Apply(image);
    }

    /// <summary>
    /// Writes the Image's original sprite (just its region, not the whole atlas) to
    /// sprites2/dumped/&lt;fileName&gt;.png. Existing files are kept unless <paramref name="overwrite"/>,
    /// so edited replacements aren't clobbered.
    /// </summary>
    /// <returns>True if a file was written.</returns>
    public static bool Dump(Image image, string fileName, bool overwrite)
    {
        var original = GetOriginalSprite(image);
        if (original == null)
            return false;

        var file = PngPath(fileName);
        if (File.Exists(file) && !overwrite)
            return false;

        var png = SpriteImages.EncodeSpriteToPng(original);
        if (png == null || png.Length == 0)
            return false;

        Directory.CreateDirectory(Path.GetDirectoryName(file));
        File.WriteAllBytes(file, png);
        return true;
    }

    /// <summary>Dumps every visible Image's sprite that hasn't been dumped yet. Returns how many.</summary>
    public static int DumpAllVisible()
    {
        var written = 0;
        var seen = new HashSet<string>();
        foreach (var image in UiCompat.FindObjectsOfType<Image>())
        {
            if (image == null || !image.gameObject.activeInHierarchy)
                continue;
            if (ObjectHelper.GetGameObjectPath(image.gameObject).StartsWith(ElementPicker.EditorObjectPrefix))
                continue;

            var original = GetOriginalSprite(image);
            if (original == null)
                continue;

            var fileName = SpriteContract.SafeFileName(original.name);
            if (!seen.Add(fileName))
                continue;

            try
            {
                if (Dump(image, fileName, overwrite: false))
                    written++;
            }
            catch (Exception ex)
            {
                WarnOnce("dump:" + fileName, $"[UIEditor] Could not dump sprite '{original.name}': {ex.Message}");
            }
        }
        return written;
    }

    private static void Revert(Replaced entry)
    {
        _replaced.Remove(entry.Id);
        if (entry.Image == null || entry.Image.sprite != entry.Replacement)
            return;

        _applying = true;
        try
        {
            entry.Image.sprite = entry.Original;
        }
        finally
        {
            _applying = false;
        }
    }

    /// <summary>The (cached) replacement sprite built from a PNG for this original, or null if the PNG is missing.</summary>
    public static Sprite GetReplacementSprite(string fileName, Sprite original) => GetReplacement(fileName, original);

    private static Sprite GetReplacement(string fileName, Sprite original)
    {
        if (string.IsNullOrEmpty(fileName))
            return null;

        var key = fileName + "|" + original.GetInstanceID();
        if (_sprites.TryGetValue(key, out var cached) && cached != null)
            return cached;

        var texture = GetTexture(fileName, original);
        if (texture == null)
            return null;

        var sprite = SpriteImages.CreateReplacement(texture, original);
        _sprites[key] = sprite;
        _ourSprites.Add(sprite.GetInstanceID());
        return sprite;
    }

    private static Texture2D GetTexture(string fileName, Sprite original)
    {
        if (_textures.TryGetValue(fileName, out var cached) && cached != null)
            return cached;

        var file = PngPath(fileName);
        if (!File.Exists(file))
        {
            WarnOnce("missing:" + fileName, $"[UIEditor] Replacement image not found: {file}");
            return null;
        }

        var filter = original.texture != null ? original.texture.filterMode : FilterMode.Bilinear;
        var texture = SpriteImages.LoadTexture(File.ReadAllBytes(file), fileName, filter);
        if (texture == null)
        {
            WarnOnce("invalid:" + fileName, $"[UIEditor] Could not read '{file}' as an image.");
            return null;
        }

        _textures[fileName] = texture;
        return texture;
    }

    // Destroys cached textures/sprites for one file (or all when null). Callers revert first,
    // so no Image is still showing them.
    private static void ClearCaches(string fileName)
    {
        foreach (var key in new List<string>(_sprites.Keys))
        {
            if (fileName != null && !key.StartsWith(fileName + "|"))
                continue;
            var sprite = _sprites[key];
            if (sprite != null)
            {
                _ourSprites.Remove(sprite.GetInstanceID());
                UnityEngine.Object.Destroy(sprite);
            }
            _sprites.Remove(key);
        }

        foreach (var key in new List<string>(_textures.Keys))
        {
            if (fileName != null && key != fileName)
                continue;
            if (_textures[key] != null)
                UnityEngine.Object.Destroy(_textures[key]);
            _textures.Remove(key);
        }
    }

    private static void PruneDestroyed()
    {
        var dead = new List<int>();
        foreach (var pair in _replaced)
        {
            if (pair.Value.Image == null)
                dead.Add(pair.Key);
        }
        foreach (var id in dead)
            _replaced.Remove(id);
    }

    private static string CachedPath(Image image)
    {
        var id = image.GetInstanceID();
        if (!_pathCache.TryGetValue(id, out var path))
        {
            path = ObjectHelper.GetGameObjectPath(image.gameObject);
            _pathCache[id] = path;
        }
        return path;
    }

    private static void WarnOnce(string key, string message)
    {
        if (_warned.Add(key))
            _logger?.LogWarning(message);
    }
}
