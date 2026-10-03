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
///   * Image.sprite is set constantly by animated images, so the hook path filters by the Image's
///     own name before building its path, and missing/unreadable PNGs are remembered rather than
///     re-checked on disk every time (until Reload / ReloadFile).
/// </summary>
internal static class SpriteApplier
{
    private sealed class Replaced
    {
        public int Id;
        public Image Image;
        public Sprite Original;
        public Sprite Replacement;
        public int ReplacementId;
        public string Path;
        public string File;
    }

    /// <summary>A replacement built for one original sprite (kept so it can be freed once the
    /// original is gone).</summary>
    private sealed class CachedSprite
    {
        public Sprite Original;
        public Sprite Replacement;
        public int ReplacementId;
    }

    private struct CachedPath
    {
        public string Path;
        public string Name;
        public int ParentId;
        public int RootId;
    }

    private static ContractRepository<SpriteContract> _repository;
    private static IPluginLogger _logger;

    private static readonly Dictionary<int, Replaced> _replaced = new Dictionary<int, Replaced>();
    private static readonly Dictionary<string, Texture2D> _textures = new Dictionary<string, Texture2D>();
    // File name -> original sprite instance ID -> replacement.
    private static readonly Dictionary<string, Dictionary<int, CachedSprite>> _sprites = new Dictionary<string, Dictionary<int, CachedSprite>>();
    private static readonly HashSet<int> _ourSprites = new HashSet<int>();
    // PNGs that were missing or unreadable, so a matching rule doesn't hit the disk on every
    // Image.sprite set. Cleared by Reload / ReloadFile.
    private static readonly HashSet<string> _badFiles = new HashSet<string>();
    // Image.sprite is set often (sprite animation); don't rebuild hierarchy paths every time. An
    // entry is trusted only while the Image's name, parent and root are unchanged (rows are often
    // instantiated at the root and then reparented), and the cache is dropped periodically.
    private static readonly Dictionary<int, CachedPath> _pathCache = new Dictionary<int, CachedPath>();
    private static readonly HashSet<string> _warned = new HashSet<string>();
    private static readonly HashSet<int> _inUse = new HashSet<int>();
    private static readonly List<int> _deadIds = new List<int>();

    private const int MaxCachedPaths = 20000;

    // Images enabled inside a hierarchy just instantiated at the scene root (root named
    // "...(Clone)"). Games commonly move and rename it straight after - e.g. Instantiate(prefab),
    // SetParent(PopRoot), name = "CreateMenu" - with no further signal, so the path seen when
    // they were enabled matches the wrong rule (or none). Checked again, fresh, on the next tick.
    private const string CloneSuffix = "(Clone)";
    private static readonly Dictionary<int, Image> _pendingCloneChecks = new Dictionary<int, Image>();
    private static readonly List<Image> _cloneCheckBuffer = new List<Image>();
    private const int MaintenanceIntervalFrames = 600;
    private static int _framesSinceMaintenance;

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
            if (_applying || !HasRules)
                return;
            // The name check first: it's cheaper than the interop cast, and rules out most graphics.
            var name = graphic.name;
            if (!_repository.CouldMatchName(name))
                return;
            var image = UiCompat.As<Image>(graphic);
            if (image == null)
                return;
            ApplyAt(image, GetCachedPath(image, name));
            if (_pendingCloneChecks.Count < MaxCachedPaths
                && image.transform.root.name.EndsWith(CloneSuffix, StringComparison.Ordinal))
                _pendingCloneChecks[image.GetInstanceID()] = image;
        };
        UiHooks.ImageSpriteSet += image =>
        {
            // _applying first: our own `image.sprite = replacement` raises this hook again.
            if (_applying || !HasRules)
                return;
            var name = image.name;
            if (_repository.CouldMatchName(name))
                ApplyAt(image, GetCachedPath(image, name));
        };

        _logger.LogInfo($"[UIEditor] Loaded {_repository.Count} sprite rule(s) from '{_repository.Folder}'.");
    }

    public static void Tick()
    {
        if (_repository == null)
            return;

        FlushCloneChecks();

        var sceneHandle = SceneManager.GetActiveScene().handle;
        if (sceneHandle != _lastSceneHandle)
        {
            _lastSceneHandle = sceneHandle;
            _framesSinceMaintenance = 0;
            _pathCache.Clear();
            PruneDestroyed();
            PruneReplacementSprites();
            ReapplyAll();
            return;
        }

        // Also catches assets a game unloads some frames after the scene change.
        if (++_framesSinceMaintenance >= MaintenanceIntervalFrames)
        {
            _framesSinceMaintenance = 0;
            _pathCache.Clear();
            // Re-check missing PNGs now and then, so one copied in by hand is picked up.
            _badFiles.Clear();
            PruneDestroyed();
            PruneReplacementSprites();
        }
    }

    public static string PngPath(string fileName) => Path.Combine(DumpFolder, fileName + ".png");

    /// <summary>Re-applies Images enabled inside a fresh clone (see _pendingCloneChecks), with
    /// their now-final paths.</summary>
    private static void FlushCloneChecks()
    {
        if (_pendingCloneChecks.Count == 0)
            return;

        var pending = _cloneCheckBuffer;
        pending.AddRange(_pendingCloneChecks.Values);
        _pendingCloneChecks.Clear();
        try
        {
            foreach (var image in pending)
            {
                // Apply with no path builds it fresh.
                if (image != null)
                    Apply(image);
            }
        }
        finally
        {
            pending.Clear();
        }
    }

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

        var candidates = _repository.FindCandidates(path);
        for (var i = 0; i < candidates.Count; i++)
        {
            var contract = candidates[i];
            if (!contract.IsEnabled())
                continue;
            if (string.IsNullOrEmpty(contract.OriginalSprite) || contract.OriginalSprite == spriteName)
                return contract;
        }
        return null;
    }

    /// <summary>As <see cref="Match(string, string)"/>, reading the sprite's name only if a
    /// candidate actually filters on it.</summary>
    private static SpriteContract Match(IReadOnlyList<SpriteContract> candidates, Sprite sprite)
    {
        string spriteName = null;
        for (var i = 0; i < candidates.Count; i++)
        {
            var contract = candidates[i];
            if (!contract.IsEnabled())
                continue;
            if (string.IsNullOrEmpty(contract.OriginalSprite))
                return contract;
            spriteName ??= sprite.name;
            if (contract.OriginalSprite == spriteName)
                return contract;
        }
        return null;
    }

    public static void Apply(Image image, string path = null)
    {
        if (_applying || image == null || !HasRules)
            return;

        if (path == null)
        {
            var name = image.name;
            if (!_repository.CouldMatchName(name))
                return;
            // Not a hook path (rescans, reloads), so build it fresh: the cached path only notices
            // the Image's own rename/reparent, not an ancestor's.
            path = ObjectHelper.GetGameObjectPath(image.gameObject);
        }

        ApplyAt(image, path);
    }

    private static void ApplyAt(Image image, string path)
    {
        if (ObjectHelper.IsEditorObjectPath(path))
            return;

        // Rule lookup (cached per path) before touching the sprite at all.
        var candidates = _repository.FindCandidates(path);
        if (candidates.Count == 0)
            return;

        var current = image.sprite;
        if (current == null || IsOurs(current))
            return;

        var contract = Match(candidates, current);
        if (contract == null)
            return;

        var replacement = GetCachedReplacement(contract.ReplacementSprite, current);
        if (replacement == null)
            return;

        // Updated in place: the game re-setting the sprite (animation) re-applies constantly.
        var id = image.GetInstanceID();
        if (!_replaced.TryGetValue(id, out var entry))
        {
            entry = new Replaced { Id = id };
            _replaced[id] = entry;
        }
        entry.Image = image;
        entry.Original = current;
        entry.Replacement = replacement.Replacement;
        entry.ReplacementId = replacement.ReplacementId;
        entry.Path = path;
        entry.File = contract.ReplacementSprite;

        _applying = true;
        try
        {
            image.sprite = replacement.Replacement;
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

        if (!HasRules)
            return;

        foreach (var image in UiCompat.FindObjectsOfType<Image>())
        {
            if (image == null)
                continue;
            // A name no rule can match would find no rule in Apply anyway.
            var name = image.name;
            if (!_repository.CouldMatchName(name))
                continue;
            // Fresh rather than cached: the user just picked this element, and an ancestor may
            // have been renamed or moved since the path was cached.
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
        _warned.Remove("invalid:" + fileName);

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
            if (ObjectHelper.IsEditorObjectPath(ObjectHelper.GetGameObjectPath(image.gameObject)))
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

        // The dump may have written PNGs that rules were already waiting for.
        if (written > 0)
            _badFiles.Clear();
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
    public static Sprite GetReplacementSprite(string fileName, Sprite original)
    {
        return original == null ? null : GetCachedReplacement(fileName, original)?.Replacement;
    }

    private static CachedSprite GetCachedReplacement(string fileName, Sprite original)
    {
        if (string.IsNullOrEmpty(fileName))
            return null;

        var originalId = original.GetInstanceID();
        if (_sprites.TryGetValue(fileName, out var byOriginal) && byOriginal.TryGetValue(originalId, out var cached))
        {
            if (cached.Replacement != null)
                return cached;

            // Destroyed behind our back (e.g. by the game); build a new one.
            _ourSprites.Remove(cached.ReplacementId);
            byOriginal.Remove(originalId);
        }

        var texture = GetTexture(fileName, original);
        if (texture == null)
            return null;

        var sprite = SpriteImages.CreateReplacement(texture, original);
        cached = new CachedSprite { Original = original, Replacement = sprite, ReplacementId = sprite.GetInstanceID() };
        if (byOriginal == null)
            _sprites[fileName] = byOriginal = new Dictionary<int, CachedSprite>();
        byOriginal[originalId] = cached;
        _ourSprites.Add(cached.ReplacementId);
        return cached;
    }

    private static Texture2D GetTexture(string fileName, Sprite original)
    {
        if (_textures.TryGetValue(fileName, out var cached) && cached != null)
            return cached;

        if (_badFiles.Contains(fileName))
            return null;

        var file = PngPath(fileName);
        if (!File.Exists(file))
        {
            _badFiles.Add(fileName);
            WarnOnce("missing:" + fileName, $"[UIEditor] Replacement image not found: {file}");
            return null;
        }

        var filter = original.texture != null ? original.texture.filterMode : FilterMode.Bilinear;
        var texture = SpriteImages.LoadTexture(File.ReadAllBytes(file), fileName, filter);
        if (texture == null)
        {
            _badFiles.Add(fileName);
            WarnOnce("invalid:" + fileName, $"[UIEditor] Could not read '{file}' as an image.");
            return null;
        }

        _textures[fileName] = texture;
        return texture;
    }

    // Destroys cached textures/sprites for one file (or all when null) and forgets that it was
    // missing/unreadable. Callers revert first, so no Image is still showing them.
    private static void ClearCaches(string fileName)
    {
        if (fileName == null)
        {
            foreach (var byOriginal in _sprites.Values)
            {
                foreach (var cached in byOriginal.Values)
                    DestroyCached(cached);
            }
            _sprites.Clear();

            foreach (var texture in _textures.Values)
            {
                if (texture != null)
                    UnityEngine.Object.Destroy(texture);
            }
            _textures.Clear();
            _badFiles.Clear();
            return;
        }

        if (_sprites.TryGetValue(fileName, out var sprites))
        {
            foreach (var cached in sprites.Values)
                DestroyCached(cached);
            _sprites.Remove(fileName);
        }

        if (_textures.TryGetValue(fileName, out var fileTexture))
        {
            if (fileTexture != null)
                UnityEngine.Object.Destroy(fileTexture);
            _textures.Remove(fileName);
        }

        _badFiles.Remove(fileName);
    }

    private static void DestroyCached(CachedSprite cached)
    {
        _ourSprites.Remove(cached.ReplacementId);
        if (cached.Replacement != null)
            UnityEngine.Object.Destroy(cached.Replacement);
    }

    /// <summary>
    /// Replacements are built per original sprite instance and marked DontUnloadUnusedAsset, so
    /// without this every original a scene ever showed would keep a replacement alive. Frees those
    /// whose original is gone (unloaded with its scene) and that no Image still shows.
    /// </summary>
    private static void PruneReplacementSprites()
    {
        if (_sprites.Count == 0)
            return;

        _inUse.Clear();
        foreach (var entry in _replaced.Values)
            _inUse.Add(entry.ReplacementId);

        foreach (var byOriginal in _sprites.Values)
        {
            _deadIds.Clear();
            foreach (var pair in byOriginal)
            {
                var cached = pair.Value;
                if ((cached.Original == null || cached.Replacement == null) && !_inUse.Contains(cached.ReplacementId))
                    _deadIds.Add(pair.Key);
            }

            foreach (var id in _deadIds)
            {
                DestroyCached(byOriginal[id]);
                byOriginal.Remove(id);
            }
        }

        _deadIds.Clear();
        _inUse.Clear();
    }

    private static void PruneDestroyed()
    {
        _deadIds.Clear();
        foreach (var pair in _replaced)
        {
            if (pair.Value.Image == null)
                _deadIds.Add(pair.Key);
        }
        foreach (var id in _deadIds)
            _replaced.Remove(id);
        _deadIds.Clear();
    }

    /// <param name="name">The Image's name, which the caller has already read.</param>
    private static string GetCachedPath(Image image, string name)
    {
        var transform = image.transform;
        var parent = transform.parent;
        var parentId = parent != null ? parent.GetInstanceID() : 0;
        var rootId = parent != null ? transform.root.GetInstanceID() : transform.GetInstanceID();

        var id = image.GetInstanceID();
        if (_pathCache.TryGetValue(id, out var cached)
            && cached.ParentId == parentId && cached.RootId == rootId && cached.Name == name)
        {
            return cached.Path;
        }

        var path = ObjectHelper.GetGameObjectPath(image.gameObject);
        if (_pathCache.Count >= MaxCachedPaths)
            _pathCache.Clear();
        _pathCache[id] = new CachedPath { Path = path, Name = name, ParentId = parentId, RootId = rootId };
        return path;
    }

    private static void WarnOnce(string key, string message)
    {
        if (_warned.Add(key))
            _logger?.LogWarning(message);
    }
}
