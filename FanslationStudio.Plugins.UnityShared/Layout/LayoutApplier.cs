using System;
using System.Collections.Generic;
using System.Text;
using FanslationStudio.Plugins.Layout;
using FanslationStudio.Plugins.Shared;
using FanslationStudio.Plugins.Support;
using FanslationStudio.Plugins.UnityShared.Editor;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace FanslationStudio.Plugins.UnityShared.Layout;

/// <summary>
/// Applies <see cref="LayoutContract"/> rules to live UI elements.
///
///   * Originals are captured the first time a rule touches an element, and every value is
///     computed from them, so re-applying a rule is idempotent.
///   * Reverting restores only the properties a rule actually changed - games often move
///     elements themselves after we first saw them, and restoring stale values would fight that.
///   * Rules are applied when elements appear or have their sprite swapped (see
///     <see cref="UiHooks"/>), on scene change, and on reload. <c>enforce: true</c> rules are
///     also re-applied every tick.
///   * <c>active:</c> changes are deferred to the tick: SetActive during another object's
///     activation (i.e. from inside the OnEnable hook) can throw.
/// </summary>
internal static class LayoutApplier
{
    [Flags]
    private enum Touched
    {
        None = 0,
        AnchorMin = 1 << 0,
        AnchorMax = 1 << 1,
        Pivot = 1 << 2,
        AnchoredPosition = 1 << 3,
        SizeDelta = 1 << 4,
        LocalPosition = 1 << 5,
        LocalScale = 1 << 6,
        Rotation = 1 << 7,
        SiblingIndex = 1 << 8,
        Active = 1 << 9,
        ImageEnabled = 1 << 10,
        PreserveAspect = 1 << 11,
        ContentSizeFitter = 1 << 12,
        LayoutGroup = 1 << 13,
        ContentSizeFitterHorizontal = 1 << 14,
        ContentSizeFitterVertical = 1 << 15,
        LayoutGroupChildControlWidth = 1 << 16,
        LayoutGroupChildControlHeight = 1 << 17,
        LayoutGroupChildForceExpandWidth = 1 << 18,
        LayoutGroupChildForceExpandHeight = 1 << 19,
        LayoutGroupSpacing = 1 << 20,
    }

    private sealed class ElementState
    {
        public RectTransform Rect;
        public string Path;
        public LayoutContract Applied;
        public Touched Touched;

        public Vector2 AnchorMin, AnchorMax, Pivot, AnchoredPosition, SizeDelta;
        public Vector3 LocalPosition, LocalScale, LocalEuler;
        public int SiblingIndex;
        public bool ActiveSelf;
        public bool? ImageEnabled, PreserveAspect, ContentSizeFitterEnabled, LayoutGroupEnabled;
        public ContentSizeFitter.FitMode? ContentSizeFitterHorizontal, ContentSizeFitterVertical;
        public bool? LayoutGroupChildControlWidth, LayoutGroupChildControlHeight;
        public bool? LayoutGroupChildForceExpandWidth, LayoutGroupChildForceExpandHeight;
        public float? LayoutGroupSpacing;

        /// <summary>Children this rule's counterRotateChildren last force-rotated, by instance ID -
        /// released (rotation restored) when they drop out of that set or the rule stops applying.</summary>
        public HashSet<int> CounterRotatedChildIds;
        public bool CounterRotateSwapped;

        /// <summary>True while this state is in <see cref="_enforced"/>.</summary>
        public bool Enforced;

        // Components looked up once (GetComponent is an interop call under IL2CPP); looked up
        // again only while missing, so one destroyed or added later is still found.
        public Image Image;
        public ContentSizeFitter Fitter;
        public LayoutGroup Group;
        public HorizontalOrVerticalLayoutGroup HorizontalOrVerticalGroup;

        // copyRectFrom / placeBefore targets, resolved once per rule and re-resolved if the rule,
        // our parent or the target itself changes.
        public LayoutContract CopySourceFor, PlaceTargetFor;
        public Transform CopySourceParent, PlaceTargetParent;
        public RectTransform CopySource;
        public Transform PlaceTarget;
    }

    private static ContractRepository<LayoutContract> _repository;
    private static IPluginLogger _logger;

    private static readonly Dictionary<int, ElementState> _states = new Dictionary<int, ElementState>();
    // States whose rule has enforce: true, re-applied every tick without scanning _states.
    private static readonly List<ElementState> _enforced = new List<ElementState>();
    private static readonly List<ElementState> _enforcedBuffer = new List<ElementState>();
    private static readonly HashSet<int> _appliedThisTick = new HashSet<int>();
    private static readonly Dictionary<int, KeyValuePair<GameObject, bool>> _pendingActive = new Dictionary<int, KeyValuePair<GameObject, bool>>();
    private static readonly List<KeyValuePair<GameObject, bool>> _pendingBuffer = new List<KeyValuePair<GameObject, bool>>();
    private static readonly HashSet<string> _warned = new HashSet<string>();
    // ContentSizeFitter.FitMode by rule text; -1 = not a valid mode.
    private static readonly Dictionary<string, int> _fitModes = new Dictionary<string, int>(StringComparer.Ordinal);

    // Reused by the hot hook paths (never re-entered: Apply sets _applying, which they check).
    private static readonly List<Transform> _chain = new List<Transform>(16);
    private static readonly List<string> _chainNames = new List<string>(16);

    // Elements enabled inside a hierarchy just instantiated at the scene root (root named
    // "...(Clone)"). Games commonly move and rename it straight after - e.g. Instantiate(prefab),
    // SetParent(PopRoot), name = "CreateMenu" - with no further signal, so the paths seen when
    // they were enabled match the wrong rules (or none). Checked again on the next tick.
    private const string CloneSuffix = "(Clone)";
    private const int MaxPendingCloneChecks = 20000;
    private static readonly Dictionary<int, Transform> _pendingCloneChecks = new Dictionary<int, Transform>();
    private static readonly List<Transform> _cloneCheckBuffer = new List<Transform>();
    private static bool _flushingCloneChecks;
    private static readonly StringBuilder _pathBuilder = new StringBuilder(128);
    private static HashSet<int> _counterRotateScratch = new HashSet<int>();
    private static readonly List<int> _deadIds = new List<int>();
    private static readonly char[] PathSeparator = { '/' };

    // Destroyed elements' states are pruned this often, or sooner once _states doubles.
    private const int PruneIntervalFrames = 300;
    private const int MinPruneCount = 256;
    private static int _framesSincePrune;
    private static int _countAfterPrune;

    private static bool _applying;
    private static int _lastSceneHandle;

    public static ContractRepository<LayoutContract> Repository => _repository;
    public static bool HasRules => _repository != null && _repository.Count > 0;

    public static void Initialize(ContractRepository<LayoutContract> repository, IPluginLogger logger)
    {
        _repository = repository;
        _logger = logger;
        _repository.Load();
        _lastSceneHandle = SceneManager.GetActiveScene().handle;

        UiHooks.GraphicEnabled += graphic =>
        {
            if (HasRules)
                OnElementEnabled(graphic.transform);
        };
        UiHooks.GameObjectActivated += gameObject =>
        {
            if (HasRules)
                OnElementEnabled(gameObject.transform);
        };
        UiHooks.ImageSpriteSet += image =>
        {
            if (HasRules)
                OnElementRefreshed(image.transform);
        };
        UiHooks.TextSet += component =>
        {
            if (HasRules)
                OnElementRefreshed(component.transform);
        };
        _logger.LogInfo($"[UIEditor] Loaded {_repository.Count} layout rule(s) from '{_repository.Folder}'.");
    }

    public static void Tick()
    {
        if (_repository == null)
            return;

        var sceneHandle = SceneManager.GetActiveScene().handle;
        if (sceneHandle != _lastSceneHandle)
        {
            _lastSceneHandle = sceneHandle;
            PruneDestroyed();
            ReapplyAll();
        }
        else if (++_framesSincePrune >= PruneIntervalFrames || _states.Count >= Math.Max(MinPruneCount, 2 * _countAfterPrune))
        {
            // Recycled/instantiated rows leave states behind for objects that are later destroyed.
            PruneDestroyed();
        }

        FlushPendingActive();
        FlushCloneChecks();

        if (_enforced.Count > 0)
        {
            // Copied: Apply can revert (and so unlist) a state whose rule was disabled.
            _enforcedBuffer.AddRange(_enforced);
            try
            {
                foreach (var state in _enforcedBuffer)
                {
                    if (state.Rect == null || state.Applied == null || !state.Applied.IsEnforced())
                    {
                        SetEnforced(state, false);
                        continue;
                    }
                    Apply(state.Rect, state.Applied, state.Path);
                }
            }
            finally
            {
                _enforcedBuffer.Clear();
            }
        }

        _appliedThisTick.Clear();
    }

    /// <summary>Reloads the YAML files and re-applies everything.</summary>
    public static void Reload()
    {
        _repository.Load();
        ReapplyAll();
        _logger.LogInfo($"[UIEditor] Reloaded {_repository.Count} layout rule(s).");
    }

    /// <summary>Reverts every element we changed, then applies the current rules to every live element.</summary>
    public static void ReapplyAll()
    {
        foreach (var state in _states.Values)
            Revert(state);

        if (!HasRules)
            return;

        foreach (var rect in UiCompat.FindObjectsOfType<RectTransform>())
        {
            // Most elements' own names can't match any rule; skip building their paths.
            if (rect == null || !_repository.CouldMatchName(rect.name))
                continue;

            var path = ObjectHelper.GetGameObjectPath(rect.gameObject);
            if (ObjectHelper.IsEditorObjectPath(path))
                continue;

            var contract = _repository.Find(path);
            if (contract != null)
                Apply(rect, contract, path);
        }
    }

    /// <summary>
    /// Called when an element becomes active. Applies rules to it and to any ancestors with rules
    /// (containers with no Graphic never get an OnEnable of their own). Each element is applied at
    /// most once per tick, however many of its children appear.
    ///
    /// Runs inside very hot engine hooks, so the chain is walked once collecting names, and a
    /// level's path is only built when its name could match a rule. The whole chain is walked
    /// every time (names are cheap): an ancestor may have been renamed or reparented since an
    /// earlier event this frame, e.g. a row instantiated, renamed, then given its text.
    /// </summary>
    public static void OnElementEnabled(Transform transform)
    {
        if (_applying || !HasRules || transform == null)
            return;

        var chain = _chain;
        var names = _chainNames;
        try
        {
            var anyCandidate = false;
            for (var current = transform; current != null; current = current.parent)
            {
                var name = current.name;
                chain.Add(current);
                names.Add(name);
                anyCandidate |= _repository.CouldMatchName(name);
            }

            // Queued whatever its current names: they may only match once moved and renamed.
            if (!_flushingCloneChecks && _pendingCloneChecks.Count < MaxPendingCloneChecks
                && names[names.Count - 1].EndsWith(CloneSuffix, StringComparison.Ordinal))
                _pendingCloneChecks[transform.GetInstanceID()] = transform;

            if (!anyCandidate || ObjectHelper.IsEditorObjectPath(names[names.Count - 1]))
                return;

            var builder = _pathBuilder;
            builder.Length = 0;
            for (var i = chain.Count - 1; i >= 0; i--)
            {
                if (builder.Length > 0)
                    builder.Append('/');
                builder.Append(names[i]);

                if (!_repository.CouldMatchName(names[i]))
                    continue;

                var levelPath = builder.ToString();
                var contract = _repository.Find(levelPath);
                if (contract == null)
                    continue;

                var rect = UiCompat.As<RectTransform>(chain[i]);
                if (rect != null && _appliedThisTick.Add(rect.GetInstanceID()))
                    Apply(rect, contract, levelPath);
            }
        }
        finally
        {
            chain.Clear();
            names.Clear();
        }
    }

    /// <summary>
    /// Called when an element's content is rebound (its sprite or text setter fires) rather than
    /// its active state changing. A recycled row (e.g. a list item) is often refreshed exactly
    /// this way - one child's Image/Text is rebound with no OnEnable or SetActive anywhere in the
    /// row - so a sibling with no signal of its own (a static background behind the label, say)
    /// would otherwise never see its rule reapplied. Checks the ancestor chain as
    /// <see cref="OnElementEnabled"/> does, then its immediate siblings too. Siblings are
    /// re-applied on every refresh, not once per tick: the game's bind code may have moved them
    /// after an earlier apply this frame (Apply only writes values that differ).
    /// </summary>
    public static void OnElementRefreshed(Transform transform)
    {
        if (_applying || !HasRules || transform == null)
            return;

        OnElementEnabled(transform);

        var parent = transform.parent;
        if (parent == null)
            return;

        // The parent's path is only built once some sibling's name could match a rule.
        string parentPath = null;
        for (var i = 0; i < parent.childCount; i++)
        {
            var child = parent.GetChild(i);
            if (child == transform)
                continue;

            var name = child.name;
            if (!_repository.CouldMatchName(name))
                continue;

            if (parentPath == null)
            {
                parentPath = ObjectHelper.GetGameObjectPath(parent.gameObject);
                if (ObjectHelper.IsEditorObjectPath(parentPath))
                    return;
            }

            var path = parentPath + "/" + name;
            var contract = _repository.Find(path);
            if (contract == null)
                continue;

            var sibling = UiCompat.As<RectTransform>(child);
            if (sibling != null)
                Apply(sibling, contract, path);
        }
    }

    public static bool IsApplied(RectTransform rect)
    {
        return rect != null && _states.TryGetValue(rect.GetInstanceID(), out var state) && state.Applied != null;
    }

    /// <summary>The element's values before any rule touched it (its live values if none has).</summary>
    public static LayoutSnapshot GetOriginal(RectTransform rect)
    {
        if (_states.TryGetValue(rect.GetInstanceID(), out var state))
        {
            return new LayoutSnapshot
            {
                AnchorMin = state.AnchorMin, AnchorMax = state.AnchorMax, Pivot = state.Pivot,
                AnchoredPosition = state.AnchoredPosition, SizeDelta = state.SizeDelta,
                LocalPosition = state.LocalPosition, LocalScale = state.LocalScale, LocalEuler = state.LocalEuler,
            };
        }

        return new LayoutSnapshot
        {
            AnchorMin = rect.anchorMin, AnchorMax = rect.anchorMax, Pivot = rect.pivot,
            AnchoredPosition = rect.anchoredPosition, SizeDelta = rect.sizeDelta,
            LocalPosition = rect.localPosition, LocalScale = rect.localScale, LocalEuler = rect.localEulerAngles,
        };
    }

    /// <summary>
    /// Re-applies rules to the elements affected by a change to the given path patterns (e.g. a
    /// rule being edited, renamed or deleted), without a full rescan when the patterns are exact
    /// paths. <paramref name="hint"/> is an element known to be affected (the selection).
    /// </summary>
    public static void Refresh(IList<string> patterns, RectTransform hint)
    {
        if (_repository == null)
            return;

        bool Matches(string path)
        {
            foreach (var pattern in patterns)
            {
                if (!string.IsNullOrEmpty(pattern) && PathPattern.IsMatch(pattern, path))
                    return true;
            }
            return false;
        }

        foreach (var state in new List<ElementState>(_states.Values))
        {
            if (state.Rect == null || !Matches(state.Path))
                continue;

            Revert(state);
            var contract = _repository.Find(state.Path);
            if (contract != null)
                Apply(state.Rect, contract, state.Path);
        }

        var anyWildcard = false;
        foreach (var pattern in patterns)
            anyWildcard |= PathPattern.IsWildcard(pattern);

        if (anyWildcard)
        {
            foreach (var rect in UiCompat.FindObjectsOfType<RectTransform>())
            {
                // A name no rule can match would find no contract below anyway.
                if (rect == null || IsApplied(rect) || !_repository.CouldMatchName(rect.name))
                    continue;
                var path = ObjectHelper.GetGameObjectPath(rect.gameObject);
                if (!Matches(path))
                    continue;
                var contract = _repository.Find(path);
                if (contract != null)
                    Apply(rect, contract, path);
            }
        }
        else if (hint != null && !IsApplied(hint))
        {
            var path = ObjectHelper.GetGameObjectPath(hint.gameObject);
            var contract = _repository.Find(path);
            if (contract != null)
                Apply(hint, contract, path);
        }
    }

    public static void Apply(RectTransform rect, LayoutContract contract, string path)
    {
        if (rect == null || contract == null)
            return;

        if (!contract.IsEnabled())
        {
            if (_states.TryGetValue(rect.GetInstanceID(), out var existing))
                Revert(existing);
            return;
        }

        var state = GetOrCapture(rect, path);

        // A different rule than last time: undo the old one first so nothing it set lingers.
        if (state.Applied != null && !ReferenceEquals(state.Applied, contract))
            Revert(state);

        _applying = true;
        try
        {
            ApplyRectTransform(state, contract);
            ApplyComponents(state, contract);
            ApplyPlaceBefore(state, contract);
            ApplyCounterRotation(state, contract);

            if (contract.Active.HasValue)
            {
                state.Touched |= Touched.Active;
                QueueSetActive(rect.gameObject, contract.Active.Value);
            }

            state.Applied = contract;
            SetEnforced(state, contract.IsEnforced());
        }
        catch (Exception ex)
        {
            WarnOnce($"apply:{contract.Path}", $"[UIEditor] Layout rule '{contract.Path}' failed on '{path}': {ex.Message}");
        }
        finally
        {
            _applying = false;
        }
    }

    // Every write below is skipped when the value is already right: enforced rules re-apply every
    // tick, and any RectTransform/layout write dirties the canvas (a rebuild) even if unchanged.
    private static void ApplyRectTransform(ElementState state, LayoutContract contract)
    {
        var rect = state.Rect;
        var anchorMin = state.AnchorMin;
        var anchorMax = state.AnchorMax;
        var pivot = state.Pivot;
        var basePosition = state.AnchoredPosition;
        var baseSize = state.SizeDelta;
        var copied = false;
        var copiedSize = false;

        if (!string.IsNullOrEmpty(contract.CopyRectFrom))
        {
            var source = ResolveCopySource(state, contract);
            if (source == null)
            {
                WarnOnce($"copy:{contract.Path}", $"[UIEditor] copyRectFrom '{contract.CopyRectFrom}' not found from '{state.Path}'.");
            }
            else
            {
                anchorMin = source.anchorMin;
                anchorMax = source.anchorMax;
                pivot = source.pivot;
                basePosition = source.anchoredPosition;
                copied = true;
                if (contract.CopySizeFromSource == true)
                {
                    baseSize = source.sizeDelta;
                    copiedSize = true;
                }
            }
        }

        // Anchors and pivot first: anchoredPosition and sizeDelta are relative to them.
        var hasAnchorMin = TryVector2(contract.AnchorMin, contract, nameof(contract.AnchorMin), out var newAnchorMin);
        if (hasAnchorMin || copied)
        {
            var value = hasAnchorMin ? newAnchorMin : anchorMin;
            if (!Same(rect.anchorMin, value))
                rect.anchorMin = value;
            state.Touched |= Touched.AnchorMin;
        }

        var hasAnchorMax = TryVector2(contract.AnchorMax, contract, nameof(contract.AnchorMax), out var newAnchorMax);
        if (hasAnchorMax || copied)
        {
            var value = hasAnchorMax ? newAnchorMax : anchorMax;
            if (!Same(rect.anchorMax, value))
                rect.anchorMax = value;
            state.Touched |= Touched.AnchorMax;
        }

        var hasPivot = TryVector2(contract.Pivot, contract, nameof(contract.Pivot), out var newPivot);
        if (hasPivot || copied)
        {
            var value = hasPivot ? newPivot : pivot;
            if (!Same(rect.pivot, value))
                rect.pivot = value;
            state.Touched |= Touched.Pivot;
        }

        var hasSize = Resolve(contract.SizeDelta, contract.OffsetSize, baseSize, contract,
            nameof(contract.SizeDelta), nameof(contract.OffsetSize), out var size);
        if (hasSize || copiedSize)
        {
            var value = hasSize ? size : baseSize;
            if (!Same(rect.sizeDelta, value))
                rect.sizeDelta = value;
            state.Touched |= Touched.SizeDelta;
        }

        var hasPosition = Resolve(contract.AnchoredPosition, contract.OffsetPosition, basePosition, contract,
            nameof(contract.AnchoredPosition), nameof(contract.OffsetPosition), out var position);
        if (hasPosition || copied)
        {
            var value = hasPosition ? position : basePosition;
            if (!Same(rect.anchoredPosition, value))
                rect.anchoredPosition = value;
            state.Touched |= Touched.AnchoredPosition;
        }

        if (TryVector3(contract.LocalPosition, contract, nameof(contract.LocalPosition), out var localPosition))
        {
            if (!Same(rect.localPosition, localPosition))
                rect.localPosition = localPosition;
            state.Touched |= Touched.LocalPosition;
        }

        if (TryVector3(contract.LocalScale, contract, nameof(contract.LocalScale), out var localScale))
        {
            if (!Same(rect.localScale, localScale))
                rect.localScale = localScale;
            state.Touched |= Touched.LocalScale;
        }

        if (contract.RotationZ.HasValue)
        {
            SetLocalEuler(rect, new Vector3(state.LocalEuler.x, state.LocalEuler.y, contract.RotationZ.Value));
            state.Touched |= Touched.Rotation;
        }
    }

    private static void ApplyComponents(ElementState state, LayoutContract contract)
    {
        if (contract.ImageEnabled.HasValue || contract.PreserveAspect.HasValue)
        {
            var image = GetImage(state);
            if (image != null && contract.ImageEnabled.HasValue)
            {
                if (image.enabled != contract.ImageEnabled.Value)
                    image.enabled = contract.ImageEnabled.Value;
                state.Touched |= Touched.ImageEnabled;
            }
            if (image != null && contract.PreserveAspect.HasValue)
            {
                if (image.preserveAspect != contract.PreserveAspect.Value)
                    image.preserveAspect = contract.PreserveAspect.Value;
                state.Touched |= Touched.PreserveAspect;
            }
        }

        if (contract.ContentSizeFitterEnabled.HasValue)
        {
            var fitter = GetFitter(state);
            if (fitter != null)
            {
                if (fitter.enabled != contract.ContentSizeFitterEnabled.Value)
                    fitter.enabled = contract.ContentSizeFitterEnabled.Value;
                state.Touched |= Touched.ContentSizeFitter;
            }
        }

        if (contract.LayoutGroupEnabled.HasValue)
        {
            var group = GetGroup(state);
            if (group != null)
            {
                if (group.enabled != contract.LayoutGroupEnabled.Value)
                    group.enabled = contract.LayoutGroupEnabled.Value;
                state.Touched |= Touched.LayoutGroup;
            }
        }

        if (contract.ContentSizeFitterHorizontal != null || contract.ContentSizeFitterVertical != null)
        {
            var fitter = GetFitter(state);
            if (fitter != null)
            {
                if (TryParseFitMode(contract.ContentSizeFitterHorizontal, out var horizontalMode))
                {
                    if (fitter.horizontalFit != horizontalMode)
                        fitter.horizontalFit = horizontalMode;
                    state.Touched |= Touched.ContentSizeFitterHorizontal;
                }
                if (TryParseFitMode(contract.ContentSizeFitterVertical, out var verticalMode))
                {
                    if (fitter.verticalFit != verticalMode)
                        fitter.verticalFit = verticalMode;
                    state.Touched |= Touched.ContentSizeFitterVertical;
                }
            }
        }

        if (contract.LayoutGroupChildControlWidth.HasValue || contract.LayoutGroupChildControlHeight.HasValue
            || contract.LayoutGroupChildForceExpandWidth.HasValue || contract.LayoutGroupChildForceExpandHeight.HasValue
            || contract.LayoutGroupSpacing.HasValue)
        {
            var group = GetHorizontalOrVerticalGroup(state);
            if (group != null)
            {
                if (contract.LayoutGroupChildControlWidth.HasValue)
                {
                    if (group.childControlWidth != contract.LayoutGroupChildControlWidth.Value)
                        group.childControlWidth = contract.LayoutGroupChildControlWidth.Value;
                    state.Touched |= Touched.LayoutGroupChildControlWidth;
                }
                if (contract.LayoutGroupChildControlHeight.HasValue)
                {
                    if (group.childControlHeight != contract.LayoutGroupChildControlHeight.Value)
                        group.childControlHeight = contract.LayoutGroupChildControlHeight.Value;
                    state.Touched |= Touched.LayoutGroupChildControlHeight;
                }
                if (contract.LayoutGroupChildForceExpandWidth.HasValue)
                {
                    if (group.childForceExpandWidth != contract.LayoutGroupChildForceExpandWidth.Value)
                        group.childForceExpandWidth = contract.LayoutGroupChildForceExpandWidth.Value;
                    state.Touched |= Touched.LayoutGroupChildForceExpandWidth;
                }
                if (contract.LayoutGroupChildForceExpandHeight.HasValue)
                {
                    if (group.childForceExpandHeight != contract.LayoutGroupChildForceExpandHeight.Value)
                        group.childForceExpandHeight = contract.LayoutGroupChildForceExpandHeight.Value;
                    state.Touched |= Touched.LayoutGroupChildForceExpandHeight;
                }
                if (contract.LayoutGroupSpacing.HasValue)
                {
                    if (group.spacing != contract.LayoutGroupSpacing.Value)
                        group.spacing = contract.LayoutGroupSpacing.Value;
                    state.Touched |= Touched.LayoutGroupSpacing;
                }
            }
        }
    }

    // Parsed once per distinct value: rules re-apply every tick, and Enum.TryParse is slow.
    private static bool TryParseFitMode(string value, out ContentSizeFitter.FitMode mode)
    {
        mode = default;
        if (string.IsNullOrEmpty(value))
            return false;

        if (!_fitModes.TryGetValue(value, out var parsed))
        {
            parsed = Enum.TryParse(value, true, out ContentSizeFitter.FitMode result) ? (int)result : -1;
            _fitModes[value] = parsed;
        }

        if (parsed < 0)
            return false;
        mode = (ContentSizeFitter.FitMode)parsed;
        return true;
    }

    private static void ApplyPlaceBefore(ElementState state, LayoutContract contract)
    {
        if (string.IsNullOrEmpty(contract.PlaceBefore))
            return;

        var rect = state.Rect;
        var target = ResolvePlaceTarget(state, contract);
        if (target == null || target.parent != rect.parent || target == rect.transform)
        {
            WarnOnce($"place:{contract.Path}", $"[UIEditor] placeBefore '{contract.PlaceBefore}' is not a sibling of '{state.Path}'.");
            return;
        }

        var index = rect.GetSiblingIndex();
        var targetIndex = target.GetSiblingIndex();
        if (index > targetIndex)
            rect.SetSiblingIndex(targetIndex);          // inserting at the target's index puts us before it
        else if (index < targetIndex - 1)
            rect.SetSiblingIndex(targetIndex - 1);      // removing us first shifts the target down by one

        state.Touched |= Touched.SiblingIndex;
    }

    // copyRectFrom / placeBefore resolve a relative path (Split + Transform.Find per segment); an
    // enforced rule would do that every tick, so the result is kept until the rule changes, the
    // element is reparented, or the target is destroyed. Not-found results are retried.
    private static RectTransform ResolveCopySource(ElementState state, LayoutContract contract)
    {
        var parent = state.Rect.parent;
        if (!ReferenceEquals(state.CopySourceFor, contract) || state.CopySource == null || state.CopySourceParent != parent)
        {
            state.CopySource = UiCompat.As<RectTransform>(ResolveRelative(state.Rect, contract.CopyRectFrom));
            state.CopySourceFor = state.CopySource != null ? contract : null;
            state.CopySourceParent = parent;
        }
        return state.CopySource;
    }

    private static Transform ResolvePlaceTarget(ElementState state, LayoutContract contract)
    {
        var parent = state.Rect.parent;
        if (!ReferenceEquals(state.PlaceTargetFor, contract) || state.PlaceTarget == null || state.PlaceTargetParent != parent)
        {
            state.PlaceTarget = ResolveRelative(state.Rect, contract.PlaceBefore);
            state.PlaceTargetFor = state.PlaceTarget != null ? contract : null;
            state.PlaceTargetParent = parent;
        }
        return state.PlaceTarget;
    }

    private static void ApplyCounterRotation(ElementState state, LayoutContract contract)
    {
        var wanted = contract.CounterRotateChildren == true && contract.RotationZ.HasValue;
        var swapSize = wanted && contract.CounterRotateSwapSize == true && IsPerpendicular(contract.RotationZ.Value);

        // Turned off, or the size swap toggled: release everything and start over. Otherwise the
        // new set is diffed against the last one below, so children that stay counter-rotated
        // aren't restored and re-rotated (two real writes, a canvas rebuild) on every apply.
        if (state.CounterRotatedChildIds != null && (!wanted || state.CounterRotateSwapped != swapSize))
            ReleaseCounterRotatedChildren(state);

        if (!wanted)
            return;

        var counterAngle = NormalizeAngle(-contract.RotationZ.Value);
        var rect = state.Rect;
        var current = _counterRotateScratch;
        current.Clear();
        for (var i = 0; i < rect.childCount; i++)
        {
            var child = UiCompat.As<RectTransform>(rect.GetChild(i));
            if (child == null)
                continue;

            var childId = child.GetInstanceID();
            var childName = child.name;
            string childPath = null;
            if (_repository.CouldMatchName(childName))
            {
                childPath = state.Path + "/" + childName;
                var childContract = _repository.Find(childPath);
                if (childContract != null && childContract.RotationZ.HasValue)
                    continue; // an explicit rule on the child wins (and a child dropping out is released below)
            }

            if (!_states.TryGetValue(childId, out var childState))
                childState = GetOrCapture(child, childPath ?? state.Path + "/" + childName);

            SetLocalEuler(child, new Vector3(childState.LocalEuler.x, childState.LocalEuler.y, counterAngle));
            childState.Touched |= Touched.Rotation;

            if (swapSize)
            {
                var swapped = new Vector2(childState.SizeDelta.y, childState.SizeDelta.x);
                if (!Same(child.sizeDelta, swapped))
                    child.sizeDelta = swapped;
                childState.Touched |= Touched.SizeDelta;

                if (childState.ContentSizeFitterEnabled == true)
                {
                    var fitter = GetFitter(childState);
                    if (fitter != null)
                    {
                        if (fitter.enabled)
                            fitter.enabled = false;
                        childState.Touched |= Touched.ContentSizeFitter;
                    }
                }
            }

            current.Add(childId);
        }

        var previous = state.CounterRotatedChildIds;
        if (previous != null)
        {
            foreach (var id in previous)
            {
                if (!current.Contains(id))
                    ReleaseCounterRotatedChild(id);
            }
            previous.Clear();
        }

        // Swap the sets: the new one is kept, the old (now empty) one is reused next time.
        if (current.Count > 0)
        {
            state.CounterRotatedChildIds = current;
            _counterRotateScratch = previous ?? new HashSet<int>();
        }
        else
        {
            state.CounterRotatedChildIds = null;
        }
        state.CounterRotateSwapped = swapSize;
    }

    /// <summary>Restores rotation (and, if swapped, size/fitter state) on every child a rule's
    /// counterRotateChildren force-touched.</summary>
    private static void ReleaseCounterRotatedChildren(ElementState state)
    {
        if (state.CounterRotatedChildIds == null)
            return;

        foreach (var id in state.CounterRotatedChildIds)
            ReleaseCounterRotatedChild(id);

        state.CounterRotatedChildIds = null;
        state.CounterRotateSwapped = false;
    }

    private static void ReleaseCounterRotatedChild(int id)
    {
        if (!_states.TryGetValue(id, out var childState) || childState.Rect == null)
            return;

        if ((childState.Touched & Touched.Rotation) != 0)
        {
            childState.Touched &= ~Touched.Rotation;
            SetLocalEuler(childState.Rect, childState.LocalEuler);
        }

        if ((childState.Touched & Touched.SizeDelta) != 0)
        {
            childState.Touched &= ~Touched.SizeDelta;
            if (!Same(childState.Rect.sizeDelta, childState.SizeDelta))
                childState.Rect.sizeDelta = childState.SizeDelta;
        }

        if ((childState.Touched & Touched.ContentSizeFitter) != 0 && childState.ContentSizeFitterEnabled.HasValue)
        {
            childState.Touched &= ~Touched.ContentSizeFitter;
            var fitter = GetFitter(childState);
            if (fitter != null && fitter.enabled != childState.ContentSizeFitterEnabled.Value)
                fitter.enabled = childState.ContentSizeFitterEnabled.Value;
        }
    }

    private static float NormalizeAngle(float angle)
    {
        angle %= 360f;
        return angle < 0 ? angle + 360f : angle;
    }

    /// <summary>True if the angle is an odd multiple of 90 degrees (i.e. local axes end up
    /// swapped relative to the screen), where swapping a child's sizeDelta actually makes sense.</summary>
    private static bool IsPerpendicular(float angle)
    {
        var halfTurn = NormalizeAngle(angle) % 180f;
        return Mathf.Abs(halfTurn - 90f) < 0.01f;
    }

    public static void Revert(RectTransform rect)
    {
        if (rect != null && _states.TryGetValue(rect.GetInstanceID(), out var state))
            Revert(state);
    }

    private static void Revert(ElementState state)
    {
        var rect = state.Rect;
        var touched = state.Touched;
        state.Applied = null;
        state.Touched = Touched.None;
        SetEnforced(state, false);

        ReleaseCounterRotatedChildren(state);

        if (rect == null || touched == Touched.None)
            return;

        _applying = true;
        try
        {
            if ((touched & Touched.AnchorMin) != 0 && !Same(rect.anchorMin, state.AnchorMin)) rect.anchorMin = state.AnchorMin;
            if ((touched & Touched.AnchorMax) != 0 && !Same(rect.anchorMax, state.AnchorMax)) rect.anchorMax = state.AnchorMax;
            if ((touched & Touched.Pivot) != 0 && !Same(rect.pivot, state.Pivot)) rect.pivot = state.Pivot;
            if ((touched & Touched.SizeDelta) != 0 && !Same(rect.sizeDelta, state.SizeDelta)) rect.sizeDelta = state.SizeDelta;
            if ((touched & Touched.AnchoredPosition) != 0 && !Same(rect.anchoredPosition, state.AnchoredPosition)) rect.anchoredPosition = state.AnchoredPosition;
            if ((touched & Touched.LocalPosition) != 0 && !Same(rect.localPosition, state.LocalPosition)) rect.localPosition = state.LocalPosition;
            if ((touched & Touched.LocalScale) != 0 && !Same(rect.localScale, state.LocalScale)) rect.localScale = state.LocalScale;
            if ((touched & Touched.Rotation) != 0) SetLocalEuler(rect, state.LocalEuler);
            if ((touched & Touched.SiblingIndex) != 0 && rect.GetSiblingIndex() != state.SiblingIndex) rect.SetSiblingIndex(state.SiblingIndex);

            if ((touched & (Touched.ImageEnabled | Touched.PreserveAspect)) != 0)
            {
                var image = GetImage(state);
                if (image != null && (touched & Touched.ImageEnabled) != 0 && state.ImageEnabled.HasValue)
                    image.enabled = state.ImageEnabled.Value;
                if (image != null && (touched & Touched.PreserveAspect) != 0 && state.PreserveAspect.HasValue)
                    image.preserveAspect = state.PreserveAspect.Value;
            }

            if ((touched & Touched.ContentSizeFitter) != 0 && state.ContentSizeFitterEnabled.HasValue)
            {
                var fitter = GetFitter(state);
                if (fitter != null)
                    fitter.enabled = state.ContentSizeFitterEnabled.Value;
            }

            if ((touched & Touched.LayoutGroup) != 0 && state.LayoutGroupEnabled.HasValue)
            {
                var group = GetGroup(state);
                if (group != null)
                    group.enabled = state.LayoutGroupEnabled.Value;
            }

            if ((touched & (Touched.ContentSizeFitterHorizontal | Touched.ContentSizeFitterVertical)) != 0)
            {
                var fitter = GetFitter(state);
                if (fitter != null)
                {
                    if ((touched & Touched.ContentSizeFitterHorizontal) != 0 && state.ContentSizeFitterHorizontal.HasValue)
                        fitter.horizontalFit = state.ContentSizeFitterHorizontal.Value;
                    if ((touched & Touched.ContentSizeFitterVertical) != 0 && state.ContentSizeFitterVertical.HasValue)
                        fitter.verticalFit = state.ContentSizeFitterVertical.Value;
                }
            }

            const Touched horizontalOrVerticalGroupFlags = Touched.LayoutGroupChildControlWidth | Touched.LayoutGroupChildControlHeight
                | Touched.LayoutGroupChildForceExpandWidth | Touched.LayoutGroupChildForceExpandHeight | Touched.LayoutGroupSpacing;
            if ((touched & horizontalOrVerticalGroupFlags) != 0)
            {
                var group = GetHorizontalOrVerticalGroup(state);
                if (group != null)
                {
                    if ((touched & Touched.LayoutGroupChildControlWidth) != 0 && state.LayoutGroupChildControlWidth.HasValue)
                        group.childControlWidth = state.LayoutGroupChildControlWidth.Value;
                    if ((touched & Touched.LayoutGroupChildControlHeight) != 0 && state.LayoutGroupChildControlHeight.HasValue)
                        group.childControlHeight = state.LayoutGroupChildControlHeight.Value;
                    if ((touched & Touched.LayoutGroupChildForceExpandWidth) != 0 && state.LayoutGroupChildForceExpandWidth.HasValue)
                        group.childForceExpandWidth = state.LayoutGroupChildForceExpandWidth.Value;
                    if ((touched & Touched.LayoutGroupChildForceExpandHeight) != 0 && state.LayoutGroupChildForceExpandHeight.HasValue)
                        group.childForceExpandHeight = state.LayoutGroupChildForceExpandHeight.Value;
                    if ((touched & Touched.LayoutGroupSpacing) != 0 && state.LayoutGroupSpacing.HasValue)
                        group.spacing = state.LayoutGroupSpacing.Value;
                }
            }

            if ((touched & Touched.Active) != 0)
                QueueSetActive(rect.gameObject, state.ActiveSelf);
        }
        catch (Exception ex)
        {
            WarnOnce($"revert:{state.Path}", $"[UIEditor] Reverting layout on '{state.Path}' failed: {ex.Message}");
        }
        finally
        {
            _applying = false;
        }
    }

    private static ElementState GetOrCapture(RectTransform rect, string path)
    {
        var id = rect.GetInstanceID();
        if (_states.TryGetValue(id, out var state))
            return state;

        var image = UiCompat.GetComponent<Image>(rect);
        var fitter = UiCompat.GetComponent<ContentSizeFitter>(rect);
        var group = UiCompat.GetComponent<LayoutGroup>(rect);
        var horizontalOrVerticalGroup = UiCompat.GetComponent<HorizontalOrVerticalLayoutGroup>(rect);

        state = new ElementState
        {
            Rect = rect,
            Path = path,
            Image = image,
            Fitter = fitter,
            Group = group,
            HorizontalOrVerticalGroup = horizontalOrVerticalGroup,
            AnchorMin = rect.anchorMin,
            AnchorMax = rect.anchorMax,
            Pivot = rect.pivot,
            AnchoredPosition = rect.anchoredPosition,
            SizeDelta = rect.sizeDelta,
            LocalPosition = rect.localPosition,
            LocalScale = rect.localScale,
            LocalEuler = rect.localEulerAngles,
            SiblingIndex = rect.GetSiblingIndex(),
            ActiveSelf = rect.gameObject.activeSelf,
            ImageEnabled = image != null ? image.enabled : (bool?)null,
            PreserveAspect = image != null ? image.preserveAspect : (bool?)null,
            ContentSizeFitterEnabled = fitter != null ? fitter.enabled : (bool?)null,
            LayoutGroupEnabled = group != null ? group.enabled : (bool?)null,
            ContentSizeFitterHorizontal = fitter != null ? fitter.horizontalFit : (ContentSizeFitter.FitMode?)null,
            ContentSizeFitterVertical = fitter != null ? fitter.verticalFit : (ContentSizeFitter.FitMode?)null,
            LayoutGroupChildControlWidth = horizontalOrVerticalGroup != null ? horizontalOrVerticalGroup.childControlWidth : (bool?)null,
            LayoutGroupChildControlHeight = horizontalOrVerticalGroup != null ? horizontalOrVerticalGroup.childControlHeight : (bool?)null,
            LayoutGroupChildForceExpandWidth = horizontalOrVerticalGroup != null ? horizontalOrVerticalGroup.childForceExpandWidth : (bool?)null,
            LayoutGroupChildForceExpandHeight = horizontalOrVerticalGroup != null ? horizontalOrVerticalGroup.childForceExpandHeight : (bool?)null,
            LayoutGroupSpacing = horizontalOrVerticalGroup != null ? horizontalOrVerticalGroup.spacing : (float?)null,
        };

        _states[id] = state;
        return state;
    }

    // Cached component lookups (non-generic on purpose: no generic interop wrappers, see the
    // instructions). A missing/destroyed component is looked up again.
    private static Image GetImage(ElementState state)
    {
        if (state.Image == null)
            state.Image = UiCompat.GetComponent<Image>(state.Rect);
        return state.Image;
    }

    private static ContentSizeFitter GetFitter(ElementState state)
    {
        if (state.Fitter == null)
            state.Fitter = UiCompat.GetComponent<ContentSizeFitter>(state.Rect);
        return state.Fitter;
    }

    private static LayoutGroup GetGroup(ElementState state)
    {
        if (state.Group == null)
            state.Group = UiCompat.GetComponent<LayoutGroup>(state.Rect);
        return state.Group;
    }

    private static HorizontalOrVerticalLayoutGroup GetHorizontalOrVerticalGroup(ElementState state)
    {
        if (state.HorizontalOrVerticalGroup == null)
            state.HorizontalOrVerticalGroup = UiCompat.GetComponent<HorizontalOrVerticalLayoutGroup>(state.Rect);
        return state.HorizontalOrVerticalGroup;
    }

    private static void SetEnforced(ElementState state, bool enforced)
    {
        if (state.Enforced == enforced)
            return;

        state.Enforced = enforced;
        if (enforced)
            _enforced.Add(state);
        else
            _enforced.Remove(state);
    }

    private static void QueueSetActive(GameObject gameObject, bool active)
    {
        _pendingActive[gameObject.GetInstanceID()] = new KeyValuePair<GameObject, bool>(gameObject, active);
    }

    /// <summary>Re-checks elements enabled inside a fresh clone (see _pendingCloneChecks) with
    /// their now-final paths.</summary>
    private static void FlushCloneChecks()
    {
        if (_pendingCloneChecks.Count == 0)
            return;

        var pending = _cloneCheckBuffer;
        pending.AddRange(_pendingCloneChecks.Values);
        _pendingCloneChecks.Clear();

        // Rules may have been applied to these under their old paths this tick; let the
        // re-check apply whatever matches now.
        _appliedThisTick.Clear();
        _flushingCloneChecks = true;
        try
        {
            foreach (var transform in pending)
            {
                if (transform != null)
                    OnElementEnabled(transform);
            }
        }
        finally
        {
            _flushingCloneChecks = false;
            pending.Clear();
        }
    }

    private static void FlushPendingActive()
    {
        if (_pendingActive.Count == 0)
            return;

        var pending = _pendingBuffer;
        pending.AddRange(_pendingActive.Values);
        _pendingActive.Clear();

        _applying = true;
        try
        {
            foreach (var entry in pending)
            {
                if (entry.Key != null && entry.Key.activeSelf != entry.Value)
                    entry.Key.SetActive(entry.Value);
            }
        }
        finally
        {
            pending.Clear();
            _applying = false;
        }
    }

    private static void PruneDestroyed()
    {
        _framesSincePrune = 0;
        _deadIds.Clear();
        foreach (var pair in _states)
        {
            if (pair.Value.Rect == null)
                _deadIds.Add(pair.Key);
        }

        foreach (var id in _deadIds)
        {
            if (_states.TryGetValue(id, out var state))
                SetEnforced(state, false);
            _states.Remove(id);
        }

        _deadIds.Clear();
        _countAfterPrune = _states.Count;
    }

    /// <summary>Resolves "../Sibling", "Child/Grandchild", "." relative to an element.</summary>
    internal static Transform ResolveRelative(Transform from, string relativePath)
    {
        var current = from;
        foreach (var segment in relativePath.Split(PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
                continue;

            current = segment == ".." ? current.parent : current.Find(segment);
            if (current == null)
                return null;
        }

        return current;
    }

    /// <summary>False if the vector is unset, or set with the wrong number of components (warned once).</summary>
    private static bool IsUsable(float[] value, int dimensions, LayoutContract contract, string field)
    {
        if (LayoutMath.IsMalformed(value, dimensions))
        {
            WarnOnce($"vector:{contract.Path}:{field}",
                $"[UIEditor] Layout rule '{contract.Path}': {field} needs {dimensions} numbers, got {value.Length}. Ignored.");
            return false;
        }

        return value != null;
    }

    private static bool TryVector2(float[] value, LayoutContract contract, string field, out Vector2 result)
    {
        result = default;
        if (!IsUsable(value, 2, contract, field))
            return false;
        result = new Vector2(value[0], value[1]);
        return true;
    }

    private static bool TryVector3(float[] value, LayoutContract contract, string field, out Vector3 result)
    {
        result = default;
        if (!IsUsable(value, 3, contract, field))
            return false;
        result = new Vector3(value[0], value[1], value[2]);
        return true;
    }

    /// <summary>
    /// <see cref="LayoutMath.Resolve"/> for a Vector2, without its array allocations (this runs
    /// every tick for enforced rules): the absolute value (else the base) plus the offset, or
    /// false when neither is set.
    /// </summary>
    private static bool Resolve(float[] absolute, float[] offset, Vector2 baseValue, LayoutContract contract,
        string absoluteField, string offsetField, out Vector2 result)
    {
        var hasAbsolute = TryVector2(absolute, contract, absoluteField, out var absoluteValue);
        var hasOffset = TryVector2(offset, contract, offsetField, out var offsetValue);
        result = hasAbsolute ? absoluteValue : baseValue;
        if (hasOffset)
            result = new Vector2(result.x + offsetValue.x, result.y + offsetValue.y);
        return hasAbsolute || hasOffset;
    }

    // Same tolerance as Unity's own Vector ==, done by hand so it's plain managed arithmetic.
    private static bool Same(Vector2 a, Vector2 b)
    {
        var dx = a.x - b.x;
        var dy = a.y - b.y;
        return dx * dx + dy * dy < 1e-10f;
    }

    private static bool Same(Vector3 a, Vector3 b)
    {
        var dx = a.x - b.x;
        var dy = a.y - b.y;
        var dz = a.z - b.z;
        return dx * dx + dy * dy + dz * dz < 1e-10f;
    }

    /// <summary>
    /// Sets localEulerAngles unless the rotation already matches. Compared as quaternions: Euler
    /// angles read back from a Transform can differ from the ones set (e.g. -90 vs 270) for the
    /// same rotation.
    /// </summary>
    private static void SetLocalEuler(Transform transform, Vector3 euler)
    {
        var current = transform.localRotation;
        var target = Quaternion.Euler(euler);
        var dot = current.x * target.x + current.y * target.y + current.z * target.z + current.w * target.w;
        // q and -q are the same rotation, hence the absolute value.
        if (Math.Abs(dot) < 1f - 1e-6f)
            transform.localEulerAngles = euler;
    }

    private static void WarnOnce(string key, string message)
    {
        if (_warned.Add(key))
            _logger?.LogWarning(message);
    }
}

/// <summary>An element's RectTransform values at a point in time.</summary>
internal struct LayoutSnapshot
{
    public Vector2 AnchorMin, AnchorMax, Pivot, AnchoredPosition, SizeDelta;
    public Vector3 LocalPosition, LocalScale, LocalEuler;
}
