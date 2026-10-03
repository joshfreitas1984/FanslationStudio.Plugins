using FanslationStudio.Plugins.Shared;
using FanslationStudio.Plugins.Support;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace FanslationStudio.Plugins.TextResizer;

public class TextResizerService
{
    internal static IPluginLogger _logger;
    public static bool _enabled = false;
    private string _resizerFolder;

    // Required Static for patches to see it
    public static bool ResizersLoaded = false;
    public static Dictionary<string, TextResizerContract> Resizers = [];

    // Explicit order: Dictionary enumeration order is not preserved once entries are removed, and
    // wildcard matching (FindAppropriateResizer) needs a stable, predictable order - the same file
    // order ContractRepository<T> uses for layout/sprite rules - rather than whatever order the
    // dictionary happens to enumerate in, which otherwise picks an arbitrary match when more than
    // one wildcard resizer matches the same path.
    private static readonly List<string> ResizersOrder = [];

    // Incremented whenever a resizer is added, removed, or renamed (i.e. whenever the set of keys
    // in Resizers changes) - never on in-place edits (PreviewResizer/typing). The editor's Rules
    // list polls this to refresh after saves, deletes and reloads.
    public static int ResizersVersion = 0;

    // Tracks which yaml file each resizer came from (an absolute file path), so edits/deletes
    // made via the editor UI rewrite the correct file instead of always appending to
    // zzAddedResizers.yaml. Resizers found across multiple files are supported - each file is
    // rewritten independently based on which resizer paths currently point at it.
    public static Dictionary<string, string> ResizerSourceFiles = [];

    // Cache for storing previously matched results
    public static Dictionary<string, TextResizerContract> CachedMatchedResizers = [];

    // Caches keyed by path or instance ID only ever grow while the game runs (every distinct
    // path/text instance seen), so they're simply dropped once they get this big.
    private const int MaxCachedEntries = 20000;

    // Texts enabled inside a hierarchy just instantiated at the scene root (root named
    // "...(Clone)"). Games commonly move and rename such a hierarchy straight after - e.g.
    // Instantiate(prefab), SetParent(PopRoot), name = "CreateMenu" - with no further signal, so
    // the path seen in OnEnable matches the wrong resizer (or none). Re-checked on the next tick.
    private const string CloneSuffix = "(Clone)";
    private static readonly Dictionary<int, TextMeshProUGUI> PendingTmpRechecks = [];
    private static readonly Dictionary<int, Text> PendingLegacyRechecks = [];
    private static readonly List<TextMeshProUGUI> PendingTmpBuffer = [];
    private static readonly List<Text> PendingLegacyBuffer = [];

    // The global "/*" resizer convention matches every path - no regex needed.
    private const string MatchAllPattern = "/*";

    // Wildcard resizers in ResizersOrder order, so FindAppropriateResizer's cache misses don't
    // re-check every exact-path resizer. Rebuilt lazily after any change (InvalidateMatches).
    private static readonly List<TextResizerContract> WildcardResizers = [];
    private static bool _wildcardResizersDirty = true;

    // Bumped by InvalidateMatches whenever any resizer changes (including previews, which don't
    // bump ResizersVersion). Resolutions with an older version re-resolve their resizer.
    private static int _matchVersion = 0;

    // What each text instance (by instance ID) last resolved to: its path and matching resizer,
    // so an unchanged path skips the resizer lookup, and so the editor's hinted RefreshMatching
    // knows which live texts exist. The path itself is rebuilt on every apply - including text
    // changes - since games instantiate, pool, move and rename rows freely and a cached path
    // silently stops the right resizer from applying.
    private sealed class Resolution
    {
        public int Id;
        public string Path;
        public bool IsEditor;
        public TextResizerContract Resizer;
        public int Version = -1;
        public TextMeshProUGUI Tmp;
        public Text Legacy;
    }

    private static readonly Dictionary<int, Resolution> TmpResolutions = [];
    private static readonly Dictionary<int, Resolution> LegacyResolutions = [];
    // Used when instance IDs aren't available, so nothing is cached.
    private static readonly Resolution UncachedResolution = new();
    private static readonly List<Resolution> ResolutionScratch = [];
    private static bool _instanceIdsAvailable = true;

    // Alignment/overflow strings parsed once per distinct value (they're applied on every text
    // change), so an invalid value is also only logged once instead of on every application.
    private static readonly Dictionary<string, TextAlignmentOptions?> ParsedAlignments = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, TextOverflowModes?> ParsedOverflowModes = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, TextAnchor?> ParsedLegacyAlignments = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, (HorizontalWrapMode?, VerticalWrapMode?)> ParsedLegacyOverflowModes = new(StringComparer.Ordinal);
    private const int MaxParsedValues = 1024;

    // Not TrimStart(' ', '\t', ...): the params overload allocates the array on every call.
    private static readonly char[] LeftTrimChars = { ' ', '\t', '\n', '\r' };

    // Flag to prevent recursion in text setter patches
    private static bool _isApplyingResizer = false;

    private static IYamlHelper _yamlHelper;

    // Host-runtime-specific attacher for the TextMetadata/LegacyTextMetadata components. See
    // IBehaviourAttacher for why this can't be a concrete type shared across Mono/IL2CPP builds.
    private static IBehaviourAttacher _behaviourAttacher;

    // Used to detect scene changes by polling instead of subscribing to SceneManager.sceneLoaded.
    // Directly subscribing a method group to that event fails under IL2CPP with a
    // MissingMethodException, because the interop-generated UnityAction<T1,T2> type doesn't
    // support the standard (object, IntPtr) delegate constructor the compiler emits.
    // Scenes are compared by handle (passed in by the host, which compiles against the real
    // assemblies): every scene not in the build settings has buildIndex -1, so changes between
    // them were missed. The build index is only kept to catch a change between Awake and the
    // first tick, as before.
    private static int _lastSceneBuildIndex = -1;
    private static int? _lastSceneHandle;

    public TextResizerService(IPluginLogger logger, bool enabled, string bepinexRootPath, IYamlHelper yamlHelper, IBehaviourAttacher behaviourAttacher)
    {
        _logger = logger;
        _enabled = enabled;
        _resizerFolder = Path.Combine(bepinexRootPath, "resizers");
        _yamlHelper = yamlHelper;
        _behaviourAttacher = behaviourAttacher;
        Instance = this;
    }

    /// <summary>The running service, for the UI Editor's Text tab. Null if TextResizer is disabled.</summary>
    public static TextResizerService Instance { get; private set; }

    // Tracks whether Harmony patching has been applied yet. Patching is deferred (see
    // EnsurePatched) rather than done immediately in Awake/Load, because under IL2CPP,
    // Harmony resolves Il2CppType tokens for patch parameter types (e.g. TextMeshProUGUI)
    // via Il2CppType.From. If this runs before Unity has naturally initialized the TMPro
    // module, it forces TextMeshProUGUI's static cctor to run reentrantly inside Il2CppInterop's
    // generic-method hook, corrupting memory (AccessViolationException) and crashing the game.
    private static bool _patched = false;

    public void Awake()
    {
        if (!_enabled)
            return;

        if (!Directory.Exists(_resizerFolder))
            Directory.CreateDirectory(_resizerFolder);

        LoadResizers();

        _lastSceneBuildIndex = SceneManager.GetActiveScene().buildIndex;

        _logger.LogWarning($"TextResizer Plugin Loaded!");
    }

    /// <summary>
    /// Applies the Harmony patches. Must be called after at least one frame/scene has run
    /// (e.g. from the plugin's Update, not from Awake/Load) so Unity has had a chance to
    /// naturally initialize TextMeshPro before Harmony/Il2CppInterop tries to resolve its
    /// type token - doing this too early crashes the game under IL2CPP.
    /// </summary>
    public void EnsurePatched()
    {
        if (_patched || !_enabled)
            return;

        Harmony.CreateAndPatchAll(typeof(TextResizerService));
        _patched = true;
        _logger.LogWarning($"TextResizer Plugin patched!");

        // Texts enabled before the hooks existed (startup/menu UI) never raised OnEnable for us,
        // so apply to everything already loaded once; the hooks cover what appears afterwards.
        if (ResizersLoaded && Resizers.Count > 0)
            ApplyAllResizers();
    }

    /// <summary>
    /// Should be called every frame (e.g. from the plugin's Update) to detect scene changes
    /// and reapply resizers. Polling is used instead of subscribing to SceneManager.sceneLoaded
    /// to avoid an IL2CPP interop MissingMethodException on the generic UnityAction delegate.
    /// </summary>
    /// <param name="sceneHandle">SceneManager.GetActiveScene().handle, read by the host.</param>
    public void CheckForSceneChange(int sceneHandle)
    {
        if (!ResizersLoaded)
            return;

        FlushPendingRechecks();

        if (_lastSceneHandle == sceneHandle)
            return;

        var firstCheck = _lastSceneHandle == null;
        _lastSceneHandle = sceneHandle;
        if (firstCheck && SceneManager.GetActiveScene().buildIndex == _lastSceneBuildIndex)
            return;

        _logger.LogDebug($"Scene changed (handle {sceneHandle}), reapplying all resizers");

        // Objects from the old scene are gone; ApplyAllResizers re-adds everything still alive.
        TmpResolutions.Clear();
        LegacyResolutions.Clear();
        ApplyAllResizers();
    }

    public void LoadResizers()
    {
        ResizersLoaded = false;

        Resizers.Clear();
        ResizerSourceFiles.Clear();
        ResizersOrder.Clear();
        InvalidateMatches();

        var resizerFiles = Directory.EnumerateFiles(_resizerFolder, "*.yaml").OrderBy(f => f, StringComparer.OrdinalIgnoreCase);
        foreach (var file in resizerFiles)
        {
            try
            {
                var content = File.ReadAllText(file);
                if (string.IsNullOrWhiteSpace(content))
                    continue;

                var newResizers = _yamlHelper.Deserialize<List<TextResizerContract>>(content);
                AddFoundResizers(newResizers, file);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error Loading resizer '{file}': {ex.ToFullString()}");
            }
        }

        InvalidateMatches();
        ResizersLoaded = true;
    }

    /// <summary>
    /// Drops every cached path -> resizer match and marks every per-instance resolution stale.
    /// Must be called whenever anything in Resizers changes (including in-place previews).
    /// </summary>
    private static void InvalidateMatches()
    {
        CachedMatchedResizers.Clear();
        _wildcardResizersDirty = true;
        _matchVersion++;
    }

    private void AddFoundResizers(List<TextResizerContract> newResizers, string sourceFile)
    {
        foreach (var newResizer in newResizers)
        {
            if (!Resizers.ContainsKey(newResizer.Path))
            {
                Resizers.Add(newResizer.Path, newResizer);
                ResizerSourceFiles[newResizer.Path] = sourceFile;
                ResizersOrder.Add(newResizer.Path);
                ResizersVersion++;
            }
        }
    }

    /// <summary>
    /// Returns all currently-loaded resizers in load order (see <see cref="ResizersOrder"/>), for
    /// display in the editor UI.
    /// </summary>
    public static List<TextResizerContract> GetAllResizers()
    {
        return ResizersOrder.Select(key => Resizers[key]).ToList();
    }

    /// <summary>
    /// Applies an in-memory-only change to a resizer (no file write) and immediately reapplies
    /// all resizers, so an in-progress edit is visible on screen right away. Used by the editor
    /// UI while the user is still editing a resizer, before they explicitly click Save.
    /// </summary>
    /// <param name="applyAll">
    /// False skips the full ApplyAllResizers scan, for callers that follow up with
    /// RefreshMatching for the affected paths anyway (the editor's throttled live preview).
    /// </param>
    public void PreviewResizer(TextResizerContract contract, bool applyAll = true)
    {
        if (!Resizers.ContainsKey(contract.Path))
            ResizersOrder.Add(contract.Path);
        Resizers[contract.Path] = contract;
        InvalidateMatches();
        if (applyAll)
            ApplyAllResizers();
    }

    /// <summary>Drops a resizer that only exists as a preview (never saved). Saved ones are kept.</summary>
    public void DiscardPreview(string path)
    {
        if (path == null || !Resizers.ContainsKey(path) || ResizerSourceFiles.ContainsKey(path))
            return;

        Resizers.Remove(path);
        ResizersOrder.Remove(path);
        InvalidateMatches();
        ResizersVersion++;
    }

    /// <summary>
    /// Reverts every text element whose path matches one of the patterns, then re-applies
    /// whatever resizer now matches it. ApplyResizing alone never undoes anything, so without
    /// this a renamed, deleted or discarded (previewed) resizer leaves its values on screen.
    /// </summary>
    public void RefreshMatching(IList<string> patterns)
    {
        InvalidateMatches();

        foreach (var textElement in FindAllTextElements())
        {
            if (textElement == null)
                continue;

            var path = ObjectHelper.GetGameObjectPath(textElement.gameObject);
            if (MatchesAny(patterns, path))
            {
                RevertToOriginal(textElement);
                ApplyResizing(textElement, path);
            }
        }

        foreach (var textElement in FindAllLegacyTextElements())
        {
            if (textElement == null)
                continue;

            var path = ObjectHelper.GetGameObjectPath(textElement.gameObject);
            if (MatchesAny(patterns, path))
            {
                RevertLegacyToOriginal(textElement);
                ApplyResizingToLegacyText(textElement, path);
            }
        }
    }

    /// <summary>
    /// Same as <see cref="RefreshMatching(IList{string})"/>, but without a full rescan when every
    /// pattern is an exact path: only the texts already seen by the hooks (the per-instance
    /// resolutions) and the given hints - elements known to be affected, e.g. the editor's
    /// selection - are refreshed. Mirrors LayoutApplier.Refresh. Either hint may be null.
    /// </summary>
    public void RefreshMatching(IList<string> patterns, TextMeshProUGUI tmpHint, Text legacyHint)
    {
        var anyWildcard = false;
        foreach (var pattern in patterns)
            anyWildcard |= PathPattern.IsWildcard(pattern);

        // Without a hint (e.g. a rule opened from the rules list) the affected element may never
        // have been seen by the hooks, so only a full rescan is sure to reach it.
        if (anyWildcard || !_instanceIdsAvailable || (tmpHint == null && legacyHint == null))
        {
            RefreshMatching(patterns);
            return;
        }

        InvalidateMatches();

        var tmpHintId = 0;
        var hasTmpHint = tmpHint != null && TryGetInstanceId(tmpHint, out tmpHintId);
        var legacyHintId = 0;
        var hasLegacyHint = legacyHint != null && TryGetInstanceId(legacyHint, out legacyHintId);

        // Snapshot first: ApplyResizing updates (and may add to) the resolution dictionaries.
        ResolutionScratch.Clear();
        foreach (var resolution in TmpResolutions.Values)
            if (resolution.Path != null && MatchesAny(patterns, resolution.Path))
                ResolutionScratch.Add(resolution);

        foreach (var resolution in ResolutionScratch)
        {
            var textElement = resolution.Tmp;
            if (textElement == null)
            {
                TmpResolutions.Remove(resolution.Id);
                continue;
            }

            if (hasTmpHint && resolution.Id == tmpHintId)
                hasTmpHint = false;

            RefreshTmp(patterns, textElement);
        }

        ResolutionScratch.Clear();
        foreach (var resolution in LegacyResolutions.Values)
            if (resolution.Path != null && MatchesAny(patterns, resolution.Path))
                ResolutionScratch.Add(resolution);

        foreach (var resolution in ResolutionScratch)
        {
            var textElement = resolution.Legacy;
            if (textElement == null)
            {
                LegacyResolutions.Remove(resolution.Id);
                continue;
            }

            if (hasLegacyHint && resolution.Id == legacyHintId)
                hasLegacyHint = false;

            RefreshLegacy(patterns, textElement);
        }

        ResolutionScratch.Clear();

        if (hasTmpHint)
            RefreshTmp(patterns, tmpHint);
        if (hasLegacyHint)
            RefreshLegacy(patterns, legacyHint);
    }

    // The cached path may be stale (reparented/renamed), so the match is rechecked on a fresh one.
    private static void RefreshTmp(IList<string> patterns, TextMeshProUGUI textElement)
    {
        var path = ObjectHelper.GetGameObjectPath(textElement.gameObject);
        if (!MatchesAny(patterns, path))
            return;

        RevertToOriginal(textElement);
        ApplyResizing(textElement, path);
    }

    private static void RefreshLegacy(IList<string> patterns, Text textElement)
    {
        var path = ObjectHelper.GetGameObjectPath(textElement.gameObject);
        if (!MatchesAny(patterns, path))
            return;

        RevertLegacyToOriginal(textElement);
        ApplyResizingToLegacyText(textElement, path);
    }

    private static bool MatchesAny(IList<string> patterns, string path)
    {
        foreach (var pattern in patterns)
        {
            if (!string.IsNullOrEmpty(pattern) && PathPattern.IsMatch(pattern, path))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Creates or updates a resizer, rewrites the owning yaml file (or zzAddedResizers.yaml for
    /// brand new resizers), and immediately re-applies all resizers so the change is visible on
    /// screen without needing a scene reload.
    /// </summary>
    /// <param name="contract">The resizer to save, with its final (possibly edited) Path.</param>
    /// <param name="previousPath">
    /// If the editor UI let the user edit Path (e.g. to add wildcards), pass the path the
    /// resizer was originally loaded/selected under so the old dictionary key is removed instead
    /// of leaving a stale duplicate entry behind. Pass null/omit for a normal in-place save.
    /// </param>
    /// <param name="applyAll">
    /// False skips the full ApplyAllResizers scan, for callers that follow up with
    /// RefreshMatching for the affected paths anyway (the editor's Save).
    /// </param>
    public void SaveResizer(TextResizerContract contract, string previousPath = null, bool applyAll = true)
    {
        var isRename = !string.IsNullOrEmpty(previousPath) && previousPath != contract.Path;
        string previousFile = null;

        if (isRename && ResizerSourceFiles.TryGetValue(previousPath, out previousFile))
        {
            Resizers.Remove(previousPath);
            ResizerSourceFiles.Remove(previousPath);
            ResizersOrder.Remove(previousPath);
        }

        if (!Resizers.ContainsKey(contract.Path))
            ResizersOrder.Add(contract.Path);
        Resizers[contract.Path] = contract;
        ResizersVersion++;
        InvalidateMatches();

        if (!ResizerSourceFiles.TryGetValue(contract.Path, out var file))
        {
            file = previousFile ?? Path.Combine(_resizerFolder, "zzAddedResizers.yaml");
            ResizerSourceFiles[contract.Path] = file;
        }

        RewriteFile(file);
        if (isRename && previousFile != null && previousFile != file)
            RewriteFile(previousFile);

        if (applyAll)
            ApplyAllResizers();

        _logger.LogWarning($"Saved resizer '{contract.Path}' to '{file}'");
    }

    /// <summary>
    /// Removes a resizer, rewrites the owning yaml file to drop it, and reverts any matching
    /// on-screen elements back to their originally-captured values.
    /// </summary>
    public void DeleteResizer(string path)
    {
        if (!Resizers.ContainsKey(path))
            return;

        Resizers.Remove(path);
        // Not Remove(key, out value): that overload is netstandard2.1-only (see ApiCompatibilityTests).
        ResizerSourceFiles.TryGetValue(path, out var sourceFile);
        ResizerSourceFiles.Remove(path);
        ResizersOrder.Remove(path);
        ResizersVersion++;
        InvalidateMatches();

        if (sourceFile != null)
            RewriteFile(sourceFile);

        foreach (var textElement in FindAllTextElements())
            if (ObjectHelper.GetGameObjectPath(textElement.gameObject) == path)
                RevertToOriginal(textElement);

        foreach (var textElement in FindAllLegacyTextElements())
            if (ObjectHelper.GetGameObjectPath(textElement.gameObject) == path)
                RevertLegacyToOriginal(textElement);

        _logger.LogWarning($"Deleted resizer '{path}'");
    }

    // Rewrites a single yaml file from scratch based on the resizers currently mapped to it in
    // ResizerSourceFiles - never appends raw serialized text, since blind string appends to a
    // yaml list can produce invalid/duplicate documents and can't support in-place edits/deletes.
    private void RewriteFile(string file)
    {
        var entries = ResizerSourceFiles
            .Where(kv => kv.Value == file)
            .Select(kv => Resizers[kv.Key])
            .ToList();

        var content = entries.Count > 0 ? _yamlHelper.Serialize(entries) : string.Empty;
        File.WriteAllText(file, content);
    }

    public static TextMeshProUGUI[] FindAllTextElements()
    {
        // UnityEngine.Object.FindObjectsOfType<T>() is generic and throws MissingMethodException
        // at runtime under IL2CPP when called from code compiled in Shared - delegate to the
        // host-specific attacher (see IBehaviourAttacher/.github/copilot-instructions.md item 4).
        return _behaviourAttacher.FindAllTextElements();
    }

    public static Text[] FindAllLegacyTextElements()
    {
        // Same reason as FindAllTextElements above.
        return _behaviourAttacher.FindAllLegacyTextElements();
    }

    /// <summary>
    /// Applies the matching resizer (if any) to a TMP text. Uses the instance's cached path,
    /// so it's cheap enough for the text setter hooks; pass refreshPath when the hierarchy may
    /// have changed since (enable/activation).
    /// </summary>
    public static void ApplyResizing(TextMeshProUGUI textComponent, bool refreshPath = false)
    {
        if (textComponent == null)
            return;

        var gameObject = textComponent.gameObject;
        if (gameObject == null)
            return;

        if (_isApplyingResizer)
            return;

        ApplyResizingCore(textComponent, gameObject, refreshPath ? ObjectHelper.GetGameObjectPath(gameObject) : null);
    }

    // freshPath: an already-built current path, or null to use the cached one.
    private static void ApplyResizing(TextMeshProUGUI textComponent, string freshPath)
    {
        if (textComponent == null)
            return;

        var gameObject = textComponent.gameObject;
        if (gameObject == null || _isApplyingResizer)
            return;

        ApplyResizingCore(textComponent, gameObject, freshPath);
    }

    private static void ApplyResizingCore(TextMeshProUGUI textComponent, GameObject gameObject, string freshPath)
    {
        var resolution = Resolve(TmpResolutions, textComponent, gameObject, freshPath);
        if (resolution != UncachedResolution)
            resolution.Tmp = textComponent;
        if (resolution.IsEditor)
            return;

        try
        {
            _isApplyingResizer = true;

            // Global tweaks, applied to every TMP text even with no resizer. Read first so an
            // already-tweaked text (the usual case on text changes) costs no setter call.
            if (textComponent.wordWrappingRatios != 1.0f)
                textComponent.wordWrappingRatios = 1.0f; //Disable Word wrapping ratios (should stop eastern rules)
            if (textComponent.enableKerning)
                textComponent.enableKerning = false;

            var resizer = resolution.Resizer;

            if (resizer == null)
                return;

            // Cache components
            var rectTransform = textComponent.rectTransform;
            var metadata = _behaviourAttacher.GetOrAttachTextMetadata(gameObject, out var wasAttached);

            // If metadata was just attached, store the original values against it
            if (wasAttached)
            {
                metadata.OriginalX = rectTransform.anchoredPosition.x;
                metadata.OriginalY = rectTransform.anchoredPosition.y;
                metadata.OriginalWidth = rectTransform.sizeDelta.x;
                metadata.OriginalHeight = rectTransform.sizeDelta.y;
                metadata.OriginalCharacterSpacing = textComponent.characterSpacing;
                metadata.OriginalLineSpacing = textComponent.lineSpacing;
                metadata.OriginalWordSpacing = textComponent.wordSpacing;
                metadata.OriginalAlignment = textComponent.alignment;
                metadata.OriginalOverflowMode = textComponent.overflowMode;
                metadata.OriginalAllowWordWrap = textComponent.enableWordWrapping;
                metadata.OriginalAllowAutoSizing = textComponent.enableAutoSizing;
                metadata.OriginalFontSize = textComponent.fontSize;
            }

            // Set this so we can debug bad resizers
            metadata.ActiveResizerPath = resizer.Path;

            // Apply position change if needed
            if (resizer.AdjustX != metadata.AdjustX
                || resizer.AdjustY != metadata.AdjustY)
            {
                metadata.AdjustX = resizer.AdjustX;
                metadata.AdjustY = resizer.AdjustY;
                rectTransform.anchoredPosition = new Vector2(metadata.OriginalX + resizer.AdjustX, metadata.OriginalY + resizer.AdjustY);
            }

            // Apply size change if needed
            if (resizer.AdjustWidth != metadata.AdjustWidth
                || resizer.AdjustHeight != metadata.AdjustHeight)
            {
                metadata.AdjustWidth = resizer.AdjustWidth;
                metadata.AdjustHeight = resizer.AdjustHeight;
                rectTransform.sizeDelta = new Vector2(metadata.OriginalWidth + metadata.AdjustWidth, metadata.OriginalHeight + metadata.AdjustHeight);
            }

            // Apply the resizing
            if (textComponent.fontSize != resizer.IdealFontSize
                && resizer.IdealFontSize != null)
            {
                textComponent.fontSize = resizer.IdealFontSize.Value;
            }
            else if (resizer.FontPercentage != null)
            {
                textComponent.fontSize = metadata.OriginalFontSize * resizer.FontPercentage ?? 1;
            }

            // Text Alignment
            var alignment = ParseAlignment(resizer);
            if (alignment.HasValue && textComponent.alignment != alignment.Value)
            {
                textComponent.alignment = alignment.Value;
            }
            else if (!alignment.HasValue && textComponent.alignment != metadata.OriginalAlignment)
            {
                textComponent.alignment = metadata.OriginalAlignment;
            }

            var overflowMode = ParseOverflowMode(resizer);
            if (overflowMode.HasValue && textComponent.overflowMode != overflowMode.Value)
            {
                textComponent.overflowMode = overflowMode.Value;
            }
            else if (!overflowMode.HasValue && textComponent.overflowMode != metadata.OriginalOverflowMode)
            {
                textComponent.overflowMode = metadata.OriginalOverflowMode;
            }

            // Toggles
            if (resizer.AllowWordWrap.HasValue
                && textComponent.enableWordWrapping != resizer.AllowWordWrap.Value)
            {
                textComponent.enableWordWrapping = resizer.AllowWordWrap.Value;
            }
            else if (!resizer.AllowWordWrap.HasValue
                && textComponent.enableWordWrapping != metadata.OriginalAllowWordWrap)
            {
                textComponent.enableWordWrapping = metadata.OriginalAllowWordWrap;
            }

            if (resizer.AllowAutoSizing.HasValue
                && textComponent.enableAutoSizing != resizer.AllowAutoSizing.Value)
            {
                textComponent.enableAutoSizing = resizer.AllowAutoSizing.Value;
            }
            else if (!resizer.AllowAutoSizing.HasValue
                && textComponent.enableAutoSizing != metadata.OriginalAllowAutoSizing)
            {
                textComponent.enableAutoSizing = metadata.OriginalAllowAutoSizing;
            }

            // Auto Sizing configuration
            if (textComponent.enableAutoSizing)
            {
                if (resizer.MinFontSize.HasValue
                    && resizer.MinFontSize != textComponent.fontSizeMin)
                {
                    textComponent.fontSizeMin = resizer.MinFontSize.Value;
                }

                if (resizer.MaxFontSize.HasValue
                    && resizer.MaxFontSize != textComponent.fontSizeMax)
                {
                    textComponent.fontSizeMax = resizer.MaxFontSize.Value;
                }
            }

            // Spacing
            if (resizer.LineSpacing.HasValue
                && resizer.LineSpacing != textComponent.lineSpacing)
            {
                textComponent.lineSpacing = resizer.LineSpacing.Value;
            }

            if (resizer.WordSpacing.HasValue
                && resizer.WordSpacing != textComponent.wordSpacing)
            {
                textComponent.wordSpacing = resizer.WordSpacing.Value;
            }

            if (resizer.CharacterSpacing.HasValue
                && resizer.CharacterSpacing != textComponent.characterSpacing)
            {
                textComponent.characterSpacing = resizer.CharacterSpacing.Value;
            }

            if (resizer.AllowLeftTrimText)
            {
                //Trim it first so when it initialises it at least trims
                var text = textComponent.text;
                var trimmed = text?.TrimStart(LeftTrimChars);
                if (text != null && trimmed.Length != text.Length)
                    textComponent.text = trimmed;
            }

            // Take out the behaviour for now to save perfrormance
            // Only add the behaviour component if it hasn't been added already
            //if (!textComponent.gameObject.TryGetComponent<TextChangedBehaviour>(out var existingBehaviour))
            //{
            //    existingBehaviour = textComponent.gameObject.AddComponent<TextChangedBehaviour>();
            //    // Set the parameter after adding the component
            //    existingBehaviour.SetOptions(resizer);
            //}
            //else if (textComponent.gameObject.TryGetComponent<TextChangedBehaviour>(out var textChangeBehavior))
            //{
            //    Destroy(textChangeBehavior);
            //}
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error applying resizer to {textComponent.name}: {ex}");
        }
        finally
        {
            _isApplyingResizer = false;
        }
    }

    /// <summary>
    /// Reverts a TextMeshProUGUI element back to the values captured before any resizer was
    /// ever applied to it. Used by DeleteResizer so removing a resizer takes visible effect
    /// immediately, instead of only affecting the next OnEnable/text change. No-op if the
    /// element never had a resizer applied (no metadata captured yet).
    /// </summary>
    public static void RevertToOriginal(TextMeshProUGUI textComponent)
    {
        if (textComponent == null || textComponent.gameObject == null)
            return;

        // TryGet, not GetOrAttach: attaching here would leave empty (all-zero) metadata behind,
        // which a later ApplyResizing would then treat as the captured originals.
        var metadata = _behaviourAttacher.TryGetTextMetadata(textComponent.gameObject);
        if (metadata == null)
            return;

        try
        {
            _isApplyingResizer = true;

            var rectTransform = textComponent.rectTransform;
            rectTransform.anchoredPosition = new Vector2(metadata.OriginalX, metadata.OriginalY);
            rectTransform.sizeDelta = new Vector2(metadata.OriginalWidth, metadata.OriginalHeight);

            textComponent.fontSize = metadata.OriginalFontSize;
            textComponent.alignment = metadata.OriginalAlignment;
            textComponent.overflowMode = metadata.OriginalOverflowMode;
            textComponent.enableWordWrapping = metadata.OriginalAllowWordWrap;
            textComponent.enableAutoSizing = metadata.OriginalAllowAutoSizing;
            textComponent.lineSpacing = metadata.OriginalLineSpacing;
            textComponent.characterSpacing = metadata.OriginalCharacterSpacing;
            textComponent.wordSpacing = metadata.OriginalWordSpacing;

            metadata.ActiveResizerPath = null;
            metadata.AdjustX = 0;
            metadata.AdjustY = 0;
            metadata.AdjustWidth = 0;
            metadata.AdjustHeight = 0;
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error reverting resizer for {textComponent.name}: {ex}");
        }
        finally
        {
            _isApplyingResizer = false;
        }
    }

    /// <summary>Legacy Text counterpart of <see cref="ApplyResizing(TextMeshProUGUI, bool)"/>.</summary>
    public static void ApplyResizingToLegacyText(Text textComponent, bool refreshPath = false)
    {
        // Legacy text has no global tweaks, so with no resizers there's nothing to do at all.
        if (textComponent == null || Resizers.Count == 0)
            return;

        var gameObject = textComponent.gameObject;
        if (gameObject == null)
            return;

        if (_isApplyingResizer)
            return;

        ApplyResizingToLegacyTextCore(textComponent, gameObject, refreshPath ? ObjectHelper.GetGameObjectPath(gameObject) : null);
    }

    private static void ApplyResizingToLegacyText(Text textComponent, string freshPath)
    {
        if (textComponent == null || Resizers.Count == 0)
            return;

        var gameObject = textComponent.gameObject;
        if (gameObject == null || _isApplyingResizer)
            return;

        ApplyResizingToLegacyTextCore(textComponent, gameObject, freshPath);
    }

    private static void ApplyResizingToLegacyTextCore(Text textComponent, GameObject gameObject, string freshPath)
    {
        var resolution = Resolve(LegacyResolutions, textComponent, gameObject, freshPath);
        if (resolution != UncachedResolution)
            resolution.Legacy = textComponent;
        if (resolution.IsEditor)
            return;

        try
        {
            _isApplyingResizer = true;

            var resizer = resolution.Resizer;

            if (resizer == null)
                return;

            // Cache components
            var rectTransform = textComponent.rectTransform;
            var metadata = _behaviourAttacher.GetOrAttachLegacyTextMetadata(gameObject, out var wasAttached);

            // If metadata was just attached, store the original values against it
            if (wasAttached)
            {
                metadata.OriginalX = rectTransform.anchoredPosition.x;
                metadata.OriginalY = rectTransform.anchoredPosition.y;
                metadata.OriginalWidth = rectTransform.sizeDelta.x;
                metadata.OriginalHeight = rectTransform.sizeDelta.y;
                metadata.OriginalLineSpacing = textComponent.lineSpacing;
                metadata.OriginalAlignment = textComponent.alignment;
                metadata.OriginalHorizontalOverflow = textComponent.horizontalOverflow;
                metadata.OriginalVerticalOverflow = textComponent.verticalOverflow;
                metadata.OriginalFontSize = textComponent.fontSize;
                metadata.OriginalResizeTextForBestFit = textComponent.resizeTextForBestFit;
                metadata.OriginalResizeTextMinSize = textComponent.resizeTextMinSize;
                metadata.OriginalResizeTextMaxSize = textComponent.resizeTextMaxSize;
            }

            // Set this so we can debug bad resizers
            metadata.ActiveResizerPath = resizer.Path;

            // Apply position change if needed
            if (resizer.AdjustX != metadata.AdjustX
                || resizer.AdjustY != metadata.AdjustY)
            {
                metadata.AdjustX = resizer.AdjustX;
                metadata.AdjustY = resizer.AdjustY;
                rectTransform.anchoredPosition = new Vector2(metadata.OriginalX + resizer.AdjustX, metadata.OriginalY + resizer.AdjustY);
            }

            // Apply size change if needed
            if (resizer.AdjustWidth != metadata.AdjustWidth
                || resizer.AdjustHeight != metadata.AdjustHeight)
            {
                metadata.AdjustWidth = resizer.AdjustWidth;
                metadata.AdjustHeight = resizer.AdjustHeight;
                rectTransform.sizeDelta = new Vector2(metadata.OriginalWidth + metadata.AdjustWidth, metadata.OriginalHeight + metadata.AdjustHeight);
            }

            // Apply the resizing
            if (textComponent.fontSize != resizer.IdealFontSize
                && resizer.IdealFontSize != null)
            {
                textComponent.fontSize = (int)resizer.IdealFontSize.Value;
            }
            else if (resizer.FontPercentage != null)
            {
                textComponent.fontSize = (int)(metadata.OriginalFontSize * resizer.FontPercentage ?? 1);
            }

            // Text Alignment - convert from TMP alignment to legacy Text alignment
            if (!string.IsNullOrEmpty(resizer.Alignment))
            {
                var alignment = ParseLegacyAlignment(resizer.Alignment);
                if (alignment.HasValue && textComponent.alignment != alignment.Value)
                {
                    textComponent.alignment = alignment.Value;
                }
            }
            else if (textComponent.alignment != metadata.OriginalAlignment)
            {
                textComponent.alignment = metadata.OriginalAlignment;
            }

            // Overflow mode - map TMP overflow to legacy Text overflow
            if (!string.IsNullOrEmpty(resizer.OverflowMode))
            {
                var (horizontal, vertical) = ParseLegacyOverflowMode(resizer.OverflowMode);
                if (horizontal.HasValue && textComponent.horizontalOverflow != horizontal.Value)
                {
                    textComponent.horizontalOverflow = horizontal.Value;
                }
                if (vertical.HasValue && textComponent.verticalOverflow != vertical.Value)
                {
                    textComponent.verticalOverflow = vertical.Value;
                }
            }
            else
            {
                if (textComponent.horizontalOverflow != metadata.OriginalHorizontalOverflow)
                    textComponent.horizontalOverflow = metadata.OriginalHorizontalOverflow;
                if (textComponent.verticalOverflow != metadata.OriginalVerticalOverflow)
                    textComponent.verticalOverflow = metadata.OriginalVerticalOverflow;
            }

            // Word Wrap
            if (resizer.AllowWordWrap.HasValue)
            {
                var horizontalOverflow = resizer.AllowWordWrap.Value ? HorizontalWrapMode.Wrap : HorizontalWrapMode.Overflow;
                if (textComponent.horizontalOverflow != horizontalOverflow)
                {
                    textComponent.horizontalOverflow = horizontalOverflow;
                }
            }

            // Auto Sizing (Best Fit)
            if (resizer.AllowAutoSizing.HasValue
                && textComponent.resizeTextForBestFit != resizer.AllowAutoSizing.Value)
            {
                textComponent.resizeTextForBestFit = resizer.AllowAutoSizing.Value;
            }
            else if (!resizer.AllowAutoSizing.HasValue
                && textComponent.resizeTextForBestFit != metadata.OriginalResizeTextForBestFit)
            {
                textComponent.resizeTextForBestFit = metadata.OriginalResizeTextForBestFit;
            }

            // Auto Sizing configuration
            if (textComponent.resizeTextForBestFit)
            {
                if (resizer.MinFontSize.HasValue
                    && resizer.MinFontSize != textComponent.resizeTextMinSize)
                {
                    textComponent.resizeTextMinSize = (int)resizer.MinFontSize.Value;
                }

                if (resizer.MaxFontSize.HasValue
                    && resizer.MaxFontSize != textComponent.resizeTextMaxSize)
                {
                    textComponent.resizeTextMaxSize = (int)resizer.MaxFontSize.Value;
                }
            }

            // Spacing
            if (resizer.LineSpacing.HasValue
                && resizer.LineSpacing != textComponent.lineSpacing)
            {
                textComponent.lineSpacing = resizer.LineSpacing.Value;
            }

            if (resizer.AllowLeftTrimText)
            {
                var text = textComponent.text;
                var trimmed = text?.TrimStart(LeftTrimChars);
                if (text != null && trimmed.Length != text.Length)
                    textComponent.text = trimmed;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error applying resizer to legacy text {textComponent.name}: {ex}");
        }
        finally
        {
            _isApplyingResizer = false;
        }
    }

    /// <summary>
    /// Reverts a legacy Text element back to the values captured before any resizer was ever
    /// applied to it. See RevertToOriginal (TMP variant) for why this exists. No-op if the
    /// element never had a resizer applied (no metadata captured yet).
    /// </summary>
    public static void RevertLegacyToOriginal(Text textComponent)
    {
        if (textComponent == null || textComponent.gameObject == null)
            return;

        // TryGet, not GetOrAttach - see RevertToOriginal.
        var metadata = _behaviourAttacher.TryGetLegacyTextMetadata(textComponent.gameObject);
        if (metadata == null)
            return;

        try
        {
            _isApplyingResizer = true;

            var rectTransform = textComponent.rectTransform;
            rectTransform.anchoredPosition = new Vector2(metadata.OriginalX, metadata.OriginalY);
            rectTransform.sizeDelta = new Vector2(metadata.OriginalWidth, metadata.OriginalHeight);

            textComponent.fontSize = metadata.OriginalFontSize;
            textComponent.alignment = metadata.OriginalAlignment;
            textComponent.horizontalOverflow = metadata.OriginalHorizontalOverflow;
            textComponent.verticalOverflow = metadata.OriginalVerticalOverflow;
            textComponent.lineSpacing = metadata.OriginalLineSpacing;
            textComponent.resizeTextForBestFit = metadata.OriginalResizeTextForBestFit;
            textComponent.resizeTextMinSize = metadata.OriginalResizeTextMinSize;
            textComponent.resizeTextMaxSize = metadata.OriginalResizeTextMaxSize;

            metadata.ActiveResizerPath = null;
            metadata.AdjustX = 0;
            metadata.AdjustY = 0;
            metadata.AdjustWidth = 0;
            metadata.AdjustHeight = 0;
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error reverting resizer for legacy text {textComponent.name}: {ex}");
        }
        finally
        {
            _isApplyingResizer = false;
        }
    }

    // The editor's Alignment dropdown offers both TMP's TextAlignmentOptions names and legacy
    // Text's own TextAnchor names (see the UI Editor's TextTab.AlignmentOptions), since a resizer's
    // Path can't be known in advance to target one or the other. A legacy-native name (e.g.
    // "UpperLeft") is used as-is; a TMP name is translated down to the nearest TextAnchor.
    private static TextAnchor? ConvertTMPAlignmentToTextAnchor(string alignment)
    {
        var fromTmpName = alignment?.ToLowerInvariant() switch
        {
            "topleft" => TextAnchor.UpperLeft,
            "top" => TextAnchor.UpperCenter,
            "topright" => TextAnchor.UpperRight,
            "left" => TextAnchor.MiddleLeft,
            "center" => TextAnchor.MiddleCenter,
            "right" => TextAnchor.MiddleRight,
            "bottomleft" => TextAnchor.LowerLeft,
            "bottom" => TextAnchor.LowerCenter,
            "bottomright" => TextAnchor.LowerRight,
            _ => (TextAnchor?)null
        };
        if (fromTmpName.HasValue)
            return fromTmpName;

        // Not one of the TMP alignment names above - check whether it's already a legacy
        // TextAnchor name (offered directly in the editor's merged Alignment dropdown).
        if (Enum.TryParse<TextAnchor>(alignment, true, out var textAnchor))
            return textAnchor;

        return null;
    }

    // The editor's Overflow Mode dropdown offers both TMP's TextOverflowModes names and legacy
    // Text's own HorizontalWrapMode/VerticalWrapMode names (see
    // TextTab.OverflowOptions), since a resizer's Path can't be known in advance
    // to target a TMP or legacy Text element. "Overflow"/"Truncate" are handled as TMP names
    // first (setting both axes, matching pre-existing saved resizers) - only names unique to the
    // legacy enums (e.g. "Wrap") fall through to being applied to their one native axis.
    private static (HorizontalWrapMode?, VerticalWrapMode?) ConvertTMPOverflowToTextOverflow(string overflowMode)
    {
        var fromTmpName = overflowMode?.ToLowerInvariant() switch
        {
            "overflow" => (HorizontalWrapMode.Overflow, VerticalWrapMode.Overflow),
            "ellipsis" => (HorizontalWrapMode.Overflow, VerticalWrapMode.Truncate),
            "truncate" => (HorizontalWrapMode.Overflow, VerticalWrapMode.Truncate),
            _ => ((HorizontalWrapMode?, VerticalWrapMode?))(null, null)
        };
        if (fromTmpName.Item1.HasValue || fromTmpName.Item2.HasValue)
            return fromTmpName;

        if (Enum.TryParse<HorizontalWrapMode>(overflowMode, true, out var horizontalWrapMode))
            return (horizontalWrapMode, null);

        if (Enum.TryParse<VerticalWrapMode>(overflowMode, true, out var verticalWrapMode))
            return (null, verticalWrapMode);

        return (null, null);
    }

    public static TextResizerContract FindAppropriateResizer(string path)
    {
        if (path == null)
            return null;

        if (Resizers.TryGetValue(path, out var tryResizer))
            return tryResizer;

        // Check cache first
        if (CachedMatchedResizers.TryGetValue(path, out var cachedResizer))
            return cachedResizer;

        if (_wildcardResizersDirty)
            RebuildWildcardResizers();

        // Try wildcard matching for the remaining resizers, in a stable load order rather than
        // Resizers' own (unordered) dictionary enumeration - otherwise, when more than one
        // wildcard resizer matches the same path, whichever the dictionary happens to enumerate
        // first wins, which looks like a random pick to the user.
        TextResizerContract match = null;
        foreach (var resizer in WildcardResizers)
        {
            if (resizer.Path == MatchAllPattern || PathPattern.IsMatch(resizer.Path, path))
            {
                match = resizer;
                break;
            }
        }

        if (CachedMatchedResizers.Count >= MaxCachedEntries)
            CachedMatchedResizers.Clear();
        CachedMatchedResizers[path] = match;
        return match;
    }

    private static void RebuildWildcardResizers()
    {
        WildcardResizers.Clear();
        foreach (var key in ResizersOrder)
        {
            if (Resizers.TryGetValue(key, out var resizer) && PathPattern.IsWildcard(resizer.Path))
                WildcardResizers.Add(resizer);
        }
        _wildcardResizersDirty = false;
    }

    // Resolves (and caches per instance) the text's path and matching resizer. freshPath, when
    // given, replaces the cached path; otherwise the path is only built on the first sighting.
    private static Resolution Resolve(Dictionary<int, Resolution> cache, Component textComponent, GameObject gameObject, string freshPath)
    {
        Resolution resolution;
        if (TryGetInstanceId(textComponent, out var id))
        {
            if (!cache.TryGetValue(id, out resolution))
            {
                if (cache.Count >= MaxCachedEntries)
                    cache.Clear();
                resolution = new Resolution { Id = id };
                cache[id] = resolution;
            }
        }
        else
        {
            resolution = UncachedResolution;
            resolution.Path = null;
        }

        if (freshPath != null || resolution.Path == null)
        {
            var path = freshPath ?? ObjectHelper.GetGameObjectPath(gameObject);
            if (path != resolution.Path)
            {
                resolution.Path = path;
                resolution.IsEditor = ObjectHelper.IsEditorObjectPath(path);
                resolution.Version = -1;
            }
        }

        if (resolution.Version != _matchVersion)
        {
            resolution.Resizer = resolution.IsEditor ? null : FindAppropriateResizer(resolution.Path);
            resolution.Version = _matchVersion;
        }

        return resolution;
    }

    // GetInstanceID isn't otherwise called from Shared, and any Unity call made from Shared can
    // fail to bind on some IL2CPP builds (see .github/copilot-instructions.md item 5). It's kept
    // in its own method so a MissingMethodException (thrown when that method is compiled) can be
    // caught here; if it happens, the per-instance cache is simply turned off.
    private static bool TryGetInstanceId(UnityEngine.Object obj, out int id)
    {
        id = 0;
        if (!_instanceIdsAvailable)
            return false;

        try
        {
            id = GetInstanceIdCore(obj);
            return true;
        }
        catch (Exception ex)
        {
            _instanceIdsAvailable = false;
            TmpResolutions.Clear();
            LegacyResolutions.Clear();
            _logger?.LogWarning($"TextResizer: GetInstanceID unavailable, text paths won't be cached: {ex.Message}");
            return false;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int GetInstanceIdCore(UnityEngine.Object obj) => obj.GetInstanceID();

    private static TextAlignmentOptions? ParseAlignment(TextResizerContract resizer)
    {
        var value = resizer.Alignment;
        if (string.IsNullOrEmpty(value))
            return null;

        if (!ParsedAlignments.TryGetValue(value, out var parsed))
        {
            parsed = Enum.TryParse<TextAlignmentOptions>(value, true, out var alignment) ? alignment : null;
            if (parsed == null)
                _logger.LogWarning($"Invalid alignment value: {value} on {resizer.Path}");
            if (ParsedAlignments.Count >= MaxParsedValues)
                ParsedAlignments.Clear();
            ParsedAlignments[value] = parsed;
        }
        return parsed;
    }

    private static TextOverflowModes? ParseOverflowMode(TextResizerContract resizer)
    {
        var value = resizer.OverflowMode;
        if (string.IsNullOrEmpty(value))
            return null;

        if (!ParsedOverflowModes.TryGetValue(value, out var parsed))
        {
            parsed = Enum.TryParse<TextOverflowModes>(value, true, out var overflowMode) ? overflowMode : null;
            if (parsed == null)
                _logger.LogWarning($"Invalid overflow value: {value} on {resizer.Path}");
            if (ParsedOverflowModes.Count >= MaxParsedValues)
                ParsedOverflowModes.Clear();
            ParsedOverflowModes[value] = parsed;
        }
        return parsed;
    }

    private static TextAnchor? ParseLegacyAlignment(string value)
    {
        if (!ParsedLegacyAlignments.TryGetValue(value, out var parsed))
        {
            parsed = ConvertTMPAlignmentToTextAnchor(value);
            if (ParsedLegacyAlignments.Count >= MaxParsedValues)
                ParsedLegacyAlignments.Clear();
            ParsedLegacyAlignments[value] = parsed;
        }
        return parsed;
    }

    private static (HorizontalWrapMode?, VerticalWrapMode?) ParseLegacyOverflowMode(string value)
    {
        if (!ParsedLegacyOverflowModes.TryGetValue(value, out var parsed))
        {
            parsed = ConvertTMPOverflowToTextOverflow(value);
            if (ParsedLegacyOverflowModes.Count >= MaxParsedValues)
                ParsedLegacyOverflowModes.Clear();
            ParsedLegacyOverflowModes[value] = parsed;
        }
        return parsed;
    }

    /// <summary>Applies resizers to every loaded text, rebuilding each one's path.</summary>
    public static void ApplyAllResizers()
    {
        foreach (var textElement in FindAllTextElements())
            ApplyResizing(textElement, refreshPath: true);

        foreach (var textElement in FindAllLegacyTextElements())
            ApplyResizingToLegacyText(textElement, refreshPath: true);
    }

    [HarmonyPostfix, HarmonyPatch(typeof(TextMeshProUGUI), "OnEnable", MethodType.Normal)]
    public static void Postfix_TMP_OnEnable(TextMeshProUGUI __instance)
    {
        if (!ResizersLoaded)
            return;

        ApplyResizing(__instance, refreshPath: true);
        if (IsInFreshClone(__instance) && TryGetInstanceId(__instance, out var id)
            && PendingTmpRechecks.Count < MaxCachedEntries)
            PendingTmpRechecks[id] = __instance;
    }

    [HarmonyPostfix, HarmonyPatch(typeof(Text), "OnEnable", MethodType.Normal)]
    public static void Postfix_Text_OnEnable(Text __instance)
    {
        if (!ResizersLoaded)
            return;

        ApplyResizingToLegacyText(__instance, refreshPath: true);
        if (IsInFreshClone(__instance) && TryGetInstanceId(__instance, out var id)
            && PendingLegacyRechecks.Count < MaxCachedEntries)
            PendingLegacyRechecks[id] = __instance;
    }

    // True when the text's hierarchy root is a just-instantiated prefab still sitting at the
    // scene root, i.e. likely to be reparented/renamed before the frame ends.
    private static bool IsInFreshClone(Component component)
    {
        if (Resizers.Count == 0 || component == null)
            return false;

        var rootName = component.transform.root.name;
        return rootName != null && rootName.EndsWith(CloneSuffix, StringComparison.Ordinal);
    }

    // Re-applies texts queued by the OnEnable postfixes, with their now-final paths.
    private static void FlushPendingRechecks()
    {
        if (PendingTmpRechecks.Count > 0)
        {
            PendingTmpBuffer.AddRange(PendingTmpRechecks.Values);
            PendingTmpRechecks.Clear();
            foreach (var text in PendingTmpBuffer)
            {
                if (text != null)
                    ApplyResizing(text, refreshPath: true);
            }
            PendingTmpBuffer.Clear();
        }

        if (PendingLegacyRechecks.Count > 0)
        {
            PendingLegacyBuffer.AddRange(PendingLegacyRechecks.Values);
            PendingLegacyRechecks.Clear();
            foreach (var text in PendingLegacyBuffer)
            {
                if (text != null)
                    ApplyResizingToLegacyText(text, refreshPath: true);
            }
            PendingLegacyBuffer.Clear();
        }
    }

    // Text setters rebuild the path too: a row instantiated or pooled elsewhere and then moved
    // (or renamed) usually only signals its final place through its text being set.
    [HarmonyPostfix, HarmonyPatch(typeof(TMP_Text), "text", MethodType.Setter)]
    public static void Postfix_TMP_SetText(TMP_Text __instance)
    {
        if (!ResizersLoaded)
            return;

        // Through the host: `is` on the IL2CPP wrapper (typed TMP_Text) would always be false.
        var tmpugui = _behaviourAttacher.AsTextMeshProUGUI(__instance);
        if (tmpugui != null)
            ApplyResizing(tmpugui, refreshPath: true);
    }

    [HarmonyPostfix, HarmonyPatch(typeof(Text), "text", MethodType.Setter)]
    public static void Postfix_Text_SetText(Text __instance)
    {
        if (!ResizersLoaded)
            return;

        ApplyResizingToLegacyText(__instance, refreshPath: true);
    }
}
