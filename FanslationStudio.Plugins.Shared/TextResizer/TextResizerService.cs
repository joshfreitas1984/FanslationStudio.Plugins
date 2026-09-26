using FanslationStudio.Plugins.Shared;
using FanslationStudio.Plugins.Support;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
    private static int _lastSceneBuildIndex = -1;

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
    }

    /// <summary>
    /// Should be called every frame (e.g. from the plugin's Update) to detect scene changes
    /// and reapply resizers. Polling is used instead of subscribing to SceneManager.sceneLoaded
    /// to avoid an IL2CPP interop MissingMethodException on the generic UnityAction delegate.
    /// </summary>
    public void CheckForSceneChange()
    {
        if (!ResizersLoaded)
            return;

        var activeScene = SceneManager.GetActiveScene();
        if (activeScene.buildIndex == _lastSceneBuildIndex)
            return;

        _lastSceneBuildIndex = activeScene.buildIndex;
        _logger.LogDebug($"Scene loaded: {activeScene.name}, reapplying all resizers");
        ApplyAllResizers();
    }

    public void LoadResizers()
    {
        ResizersLoaded = false;

        Resizers.Clear();
        ResizerSourceFiles.Clear();
        CachedMatchedResizers.Clear();
        ResizersOrder.Clear();

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

        ResizersLoaded = true;
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
    public void PreviewResizer(TextResizerContract contract)
    {
        if (!Resizers.ContainsKey(contract.Path))
            ResizersOrder.Add(contract.Path);
        Resizers[contract.Path] = contract;
        CachedMatchedResizers.Clear();
        ApplyAllResizers();
    }

    /// <summary>Drops a resizer that only exists as a preview (never saved). Saved ones are kept.</summary>
    public void DiscardPreview(string path)
    {
        if (path == null || !Resizers.ContainsKey(path) || ResizerSourceFiles.ContainsKey(path))
            return;

        Resizers.Remove(path);
        ResizersOrder.Remove(path);
        CachedMatchedResizers.Clear();
        ResizersVersion++;
    }

    /// <summary>
    /// Reverts every text element whose path matches one of the patterns, then re-applies
    /// whatever resizer now matches it. ApplyResizing alone never undoes anything, so without
    /// this a renamed, deleted or discarded (previewed) resizer leaves its values on screen.
    /// </summary>
    public void RefreshMatching(IList<string> patterns)
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

        CachedMatchedResizers.Clear();

        foreach (var textElement in FindAllTextElements())
        {
            if (textElement != null && Matches(ObjectHelper.GetGameObjectPath(textElement.gameObject)))
            {
                RevertToOriginal(textElement);
                ApplyResizing(textElement);
            }
        }

        foreach (var textElement in FindAllLegacyTextElements())
        {
            if (textElement != null && Matches(ObjectHelper.GetGameObjectPath(textElement.gameObject)))
            {
                RevertLegacyToOriginal(textElement);
                ApplyResizingToLegacyText(textElement);
            }
        }
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
    public void SaveResizer(TextResizerContract contract, string previousPath = null)
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
        CachedMatchedResizers.Clear();

        if (!ResizerSourceFiles.TryGetValue(contract.Path, out var file))
        {
            file = previousFile ?? Path.Combine(_resizerFolder, "zzAddedResizers.yaml");
            ResizerSourceFiles[contract.Path] = file;
        }

        RewriteFile(file);
        if (isRename && previousFile != null && previousFile != file)
            RewriteFile(previousFile);

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
        CachedMatchedResizers.Clear();

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

    public static void ApplyResizing(TextMeshProUGUI textComponent)
    {
        if (textComponent == null)
            return;

        if (textComponent.gameObject == null)
            return;

        if (_isApplyingResizer)
            return;

        var path = ObjectHelper.GetGameObjectPath(textComponent.gameObject);
        if (path.StartsWith(ObjectHelper.EditorObjectPrefix))
            return;

        try
        {
            _isApplyingResizer = true;

            textComponent.wordWrappingRatios = 1.0f; //Disable Word wrapping ratios (should stop eastern rules)
            textComponent.enableKerning = false;

            var resizer = FindAppropriateResizer(path);

            if (resizer == null)
                return;

            // Cache components
            var rectTransform = textComponent.rectTransform;
            var metadata = _behaviourAttacher.GetOrAttachTextMetadata(textComponent.gameObject, out var wasAttached);

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
            var validAlignment = Enum.TryParse<TextAlignmentOptions>(resizer.Alignment, true, out var alignment);
            if (resizer.Alignment != string.Empty && !validAlignment)
                _logger.LogWarning($"Invalid alignment value: {resizer.Alignment} on {resizer.Path}");

            if (validAlignment && textComponent.alignment != alignment)
            {
                textComponent.alignment = alignment;
            }
            else if (!validAlignment && textComponent.alignment != metadata.OriginalAlignment)
            {
                textComponent.alignment = metadata.OriginalAlignment;
            }

            var validOverflow = Enum.TryParse<TextOverflowModes>(resizer.OverflowMode, true, out var overflowMode);
            if (resizer.OverflowMode != string.Empty && !validOverflow)
                _logger.LogWarning($"Invalid overflow value: {resizer.OverflowMode} on {resizer.Path}");

            if (validOverflow && textComponent.overflowMode != overflowMode)
            {
                textComponent.overflowMode = overflowMode;
            }
            else if (!validOverflow && textComponent.overflowMode != metadata.OriginalOverflowMode)
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
                var trimmed = textComponent.text.TrimStart(' ', '\t', '\n', '\r');
                if (textComponent.text != trimmed)
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

        var metadata = _behaviourAttacher.GetOrAttachTextMetadata(textComponent.gameObject, out var wasAttached);
        if (wasAttached)
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

    public static void ApplyResizingToLegacyText(Text textComponent)
    {
        if (textComponent == null)
            return;

        if (textComponent.gameObject == null)
            return;

        if (_isApplyingResizer)
            return;

        var path = ObjectHelper.GetGameObjectPath(textComponent.gameObject);
        if (path.StartsWith(ObjectHelper.EditorObjectPrefix))
            return;

        try
        {
            _isApplyingResizer = true;

            var resizer = FindAppropriateResizer(path);

            if (resizer == null)
                return;

            // Cache components
            var rectTransform = textComponent.rectTransform;
            var metadata = _behaviourAttacher.GetOrAttachLegacyTextMetadata(textComponent.gameObject, out var wasAttached);

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
                var alignment = ConvertTMPAlignmentToTextAnchor(resizer.Alignment);
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
                var (horizontal, vertical) = ConvertTMPOverflowToTextOverflow(resizer.OverflowMode);
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
                var trimmed = textComponent.text.TrimStart(' ', '\t', '\n', '\r');
                if (textComponent.text != trimmed)
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

        var metadata = _behaviourAttacher.GetOrAttachLegacyTextMetadata(textComponent.gameObject, out var wasAttached);
        if (wasAttached)
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
        var fromTmpName = alignment?.ToLower() switch
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
        var fromTmpName = overflowMode?.ToLower() switch
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
        if (Resizers.TryGetValue(path, out var tryResizer))
            return tryResizer;

        // Check cache first
        if (CachedMatchedResizers.TryGetValue(path, out var cachedResizer))
            return cachedResizer;

        // Try wildcard matching for the remaining resizers, in a stable load order rather than
        // Resizers' own (unordered) dictionary enumeration - otherwise, when more than one
        // wildcard resizer matches the same path, whichever the dictionary happens to enumerate
        // first wins, which looks like a random pick to the user.
        foreach (var key in ResizersOrder)
        {
            var resizer = Resizers[key];

            if (PathPattern.IsWildcard(resizer.Path) && PathPattern.IsMatch(resizer.Path, path))
            {
                CachedMatchedResizers[path] = resizer;
                return resizer;
            }
        }

        CachedMatchedResizers[path] = null;
        return null;
    }

    public static void ApplyAllResizers()
    {
        foreach (var textElement in FindAllTextElements())
            ApplyResizing(textElement);

        foreach (var textElement in FindAllLegacyTextElements())
            ApplyResizingToLegacyText(textElement);
    }

    [HarmonyPostfix, HarmonyPatch(typeof(TextMeshProUGUI), "OnEnable", MethodType.Normal)]
    public static void Postfix_TMP_OnEnable(TextMeshProUGUI __instance)
    {
        if (!ResizersLoaded)
            return;

        ApplyResizing(__instance);
    }

    [HarmonyPostfix, HarmonyPatch(typeof(Text), "OnEnable", MethodType.Normal)]
    public static void Postfix_Text_OnEnable(Text __instance)
    {
        if (!ResizersLoaded)
            return;

        ApplyResizingToLegacyText(__instance);
    }

    [HarmonyPostfix, HarmonyPatch(typeof(TMP_Text), "text", MethodType.Setter)]
    public static void Postfix_TMP_SetText(TMP_Text __instance)
    {
        if (!ResizersLoaded)
            return;

        if (__instance is TextMeshProUGUI tmpugui)
            ApplyResizing(tmpugui);
    }

    [HarmonyPostfix, HarmonyPatch(typeof(Text), "text", MethodType.Setter)]
    public static void Postfix_Text_SetText(Text __instance)
    {
        if (!ResizersLoaded)
            return;

        ApplyResizingToLegacyText(__instance);
    }
}
