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
        /// released (rotation restored) whenever that set changes or the rule stops applying.</summary>
        public HashSet<int> CounterRotatedChildIds;
    }

    private static ContractRepository<LayoutContract> _repository;
    private static IPluginLogger _logger;

    private static readonly Dictionary<int, ElementState> _states = new Dictionary<int, ElementState>();
    private static readonly HashSet<int> _appliedThisTick = new HashSet<int>();
    private static readonly Dictionary<int, KeyValuePair<GameObject, bool>> _pendingActive = new Dictionary<int, KeyValuePair<GameObject, bool>>();
    private static readonly HashSet<string> _warned = new HashSet<string>();

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

        FlushPendingActive();

        foreach (var state in new List<ElementState>(_states.Values))
        {
            if (state.Applied != null && state.Applied.IsEnforced() && state.Rect != null)
                Apply(state.Rect, state.Applied, state.Path);
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
            if (rect == null)
                continue;

            var path = ObjectHelper.GetGameObjectPath(rect.gameObject);
            if (path.StartsWith(ElementPicker.EditorObjectPrefix))
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
    /// </summary>
    public static void OnElementEnabled(Transform transform)
    {
        if (_applying || !HasRules || transform == null)
            return;

        var chain = new List<Transform>();
        for (var current = transform; current != null; current = current.parent)
            chain.Add(current);
        chain.Reverse();

        if (chain[0].name.StartsWith(ElementPicker.EditorObjectPrefix))
            return;

        var path = new StringBuilder();
        foreach (var level in chain)
        {
            if (path.Length > 0)
                path.Append('/');
            path.Append(level.name);

            var levelPath = path.ToString();
            var contract = _repository.Find(levelPath);
            if (contract == null)
                continue;

            var rect = UiCompat.As<RectTransform>(level);
            if (rect != null && _appliedThisTick.Add(rect.GetInstanceID()))
                Apply(rect, contract, levelPath);
        }
    }

    /// <summary>
    /// Called when an element's content is rebound (its sprite or text setter fires) rather than
    /// its active state changing. A recycled row (e.g. a list item) is often refreshed exactly
    /// this way - one child's Image/Text is rebound with no OnEnable or SetActive anywhere in the
    /// row - so a sibling with no signal of its own (a static background behind the label, say)
    /// would otherwise never see its rule reapplied. Checks the ancestor chain as
    /// <see cref="OnElementEnabled"/> does, then its immediate siblings too.
    /// </summary>
    public static void OnElementRefreshed(Transform transform)
    {
        if (_applying || !HasRules || transform == null)
            return;

        OnElementEnabled(transform);

        var parent = transform.parent;
        if (parent == null || transform.root.name.StartsWith(ElementPicker.EditorObjectPrefix))
            return;

        var parentPath = ObjectHelper.GetGameObjectPath(parent.gameObject);
        for (var i = 0; i < parent.childCount; i++)
        {
            var child = parent.GetChild(i);
            if (child == transform)
                continue;

            var sibling = UiCompat.As<RectTransform>(child);
            if (sibling == null)
                continue;

            var path = parentPath + "/" + sibling.name;
            var contract = _repository.Find(path);
            if (contract != null)
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
                if (rect == null || IsApplied(rect))
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
            var source = UiCompat.As<RectTransform>(ResolveRelative(rect, contract.CopyRectFrom));
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
        var newAnchorMin = Vector(contract.AnchorMin, 2, contract, nameof(contract.AnchorMin));
        if (newAnchorMin != null || copied)
        {
            rect.anchorMin = newAnchorMin != null ? ToVector2(newAnchorMin) : anchorMin;
            state.Touched |= Touched.AnchorMin;
        }

        var newAnchorMax = Vector(contract.AnchorMax, 2, contract, nameof(contract.AnchorMax));
        if (newAnchorMax != null || copied)
        {
            rect.anchorMax = newAnchorMax != null ? ToVector2(newAnchorMax) : anchorMax;
            state.Touched |= Touched.AnchorMax;
        }

        var newPivot = Vector(contract.Pivot, 2, contract, nameof(contract.Pivot));
        if (newPivot != null || copied)
        {
            rect.pivot = newPivot != null ? ToVector2(newPivot) : pivot;
            state.Touched |= Touched.Pivot;
        }

        var size = LayoutMath.Resolve(
            Vector(contract.SizeDelta, 2, contract, nameof(contract.SizeDelta)),
            Vector(contract.OffsetSize, 2, contract, nameof(contract.OffsetSize)),
            ToArray(baseSize));
        if (size != null || copiedSize)
        {
            rect.sizeDelta = size != null ? ToVector2(size) : baseSize;
            state.Touched |= Touched.SizeDelta;
        }

        var position = LayoutMath.Resolve(
            Vector(contract.AnchoredPosition, 2, contract, nameof(contract.AnchoredPosition)),
            Vector(contract.OffsetPosition, 2, contract, nameof(contract.OffsetPosition)),
            ToArray(basePosition));
        if (position != null || copied)
        {
            rect.anchoredPosition = position != null ? ToVector2(position) : basePosition;
            state.Touched |= Touched.AnchoredPosition;
        }

        var localPosition = Vector(contract.LocalPosition, 3, contract, nameof(contract.LocalPosition));
        if (localPosition != null)
        {
            rect.localPosition = ToVector3(localPosition);
            state.Touched |= Touched.LocalPosition;
        }

        var localScale = Vector(contract.LocalScale, 3, contract, nameof(contract.LocalScale));
        if (localScale != null)
        {
            rect.localScale = ToVector3(localScale);
            state.Touched |= Touched.LocalScale;
        }

        if (contract.RotationZ.HasValue)
        {
            rect.localEulerAngles = new Vector3(state.LocalEuler.x, state.LocalEuler.y, contract.RotationZ.Value);
            state.Touched |= Touched.Rotation;
        }
    }

    private static void ApplyComponents(ElementState state, LayoutContract contract)
    {
        if (contract.ImageEnabled.HasValue || contract.PreserveAspect.HasValue)
        {
            var image = UiCompat.GetComponent<Image>(state.Rect);
            if (image != null && contract.ImageEnabled.HasValue)
            {
                image.enabled = contract.ImageEnabled.Value;
                state.Touched |= Touched.ImageEnabled;
            }
            if (image != null && contract.PreserveAspect.HasValue)
            {
                image.preserveAspect = contract.PreserveAspect.Value;
                state.Touched |= Touched.PreserveAspect;
            }
        }

        if (contract.ContentSizeFitterEnabled.HasValue)
        {
            var fitter = UiCompat.GetComponent<ContentSizeFitter>(state.Rect);
            if (fitter != null)
            {
                fitter.enabled = contract.ContentSizeFitterEnabled.Value;
                state.Touched |= Touched.ContentSizeFitter;
            }
        }

        if (contract.LayoutGroupEnabled.HasValue)
        {
            var group = UiCompat.GetComponent<LayoutGroup>(state.Rect);
            if (group != null)
            {
                group.enabled = contract.LayoutGroupEnabled.Value;
                state.Touched |= Touched.LayoutGroup;
            }
        }

        if (contract.ContentSizeFitterHorizontal != null || contract.ContentSizeFitterVertical != null)
        {
            var fitter = UiCompat.GetComponent<ContentSizeFitter>(state.Rect);
            if (fitter != null)
            {
                if (TryParseFitMode(contract.ContentSizeFitterHorizontal, out var horizontalMode))
                {
                    fitter.horizontalFit = horizontalMode;
                    state.Touched |= Touched.ContentSizeFitterHorizontal;
                }
                if (TryParseFitMode(contract.ContentSizeFitterVertical, out var verticalMode))
                {
                    fitter.verticalFit = verticalMode;
                    state.Touched |= Touched.ContentSizeFitterVertical;
                }
            }
        }

        if (contract.LayoutGroupChildControlWidth.HasValue || contract.LayoutGroupChildControlHeight.HasValue
            || contract.LayoutGroupChildForceExpandWidth.HasValue || contract.LayoutGroupChildForceExpandHeight.HasValue
            || contract.LayoutGroupSpacing.HasValue)
        {
            var group = UiCompat.GetComponent<HorizontalOrVerticalLayoutGroup>(state.Rect);
            if (group != null)
            {
                if (contract.LayoutGroupChildControlWidth.HasValue)
                {
                    group.childControlWidth = contract.LayoutGroupChildControlWidth.Value;
                    state.Touched |= Touched.LayoutGroupChildControlWidth;
                }
                if (contract.LayoutGroupChildControlHeight.HasValue)
                {
                    group.childControlHeight = contract.LayoutGroupChildControlHeight.Value;
                    state.Touched |= Touched.LayoutGroupChildControlHeight;
                }
                if (contract.LayoutGroupChildForceExpandWidth.HasValue)
                {
                    group.childForceExpandWidth = contract.LayoutGroupChildForceExpandWidth.Value;
                    state.Touched |= Touched.LayoutGroupChildForceExpandWidth;
                }
                if (contract.LayoutGroupChildForceExpandHeight.HasValue)
                {
                    group.childForceExpandHeight = contract.LayoutGroupChildForceExpandHeight.Value;
                    state.Touched |= Touched.LayoutGroupChildForceExpandHeight;
                }
                if (contract.LayoutGroupSpacing.HasValue)
                {
                    group.spacing = contract.LayoutGroupSpacing.Value;
                    state.Touched |= Touched.LayoutGroupSpacing;
                }
            }
        }
    }

    private static bool TryParseFitMode(string value, out ContentSizeFitter.FitMode mode)
    {
        if (string.IsNullOrEmpty(value))
        {
            mode = default;
            return false;
        }
        return Enum.TryParse(value, true, out mode);
    }

    private static void ApplyPlaceBefore(ElementState state, LayoutContract contract)
    {
        if (string.IsNullOrEmpty(contract.PlaceBefore))
            return;

        var rect = state.Rect;
        var target = ResolveRelative(rect, contract.PlaceBefore);
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

    private static void ApplyCounterRotation(ElementState state, LayoutContract contract)
    {
        // Always release last time's set first: recomputing from scratch (rather than diffing
        // against it) means a child dropped from consideration - the rule turned the feature off,
        // its rotationZ was removed, or the child gained its own explicit rule - reliably gets its
        // rotation restored instead of being left force-rotated forever.
        ReleaseCounterRotatedChildren(state);

        if (contract.CounterRotateChildren != true || !contract.RotationZ.HasValue)
            return;

        var counterAngle = NormalizeAngle(-contract.RotationZ.Value);
        var swapSize = contract.CounterRotateSwapSize == true && IsPerpendicular(contract.RotationZ.Value);
        var rect = state.Rect;
        HashSet<int> applied = null;
        for (var i = 0; i < rect.childCount; i++)
        {
            var child = UiCompat.As<RectTransform>(rect.GetChild(i));
            if (child == null)
                continue;

            var childPath = state.Path + "/" + child.name;
            var childContract = _repository.Find(childPath);
            if (childContract != null && childContract.RotationZ.HasValue)
                continue; // an explicit rule on the child wins

            var childState = GetOrCapture(child, childPath);
            child.localEulerAngles = new Vector3(childState.LocalEuler.x, childState.LocalEuler.y, counterAngle);
            childState.Touched |= Touched.Rotation;

            if (swapSize)
            {
                child.sizeDelta = new Vector2(childState.SizeDelta.y, childState.SizeDelta.x);
                childState.Touched |= Touched.SizeDelta;

                var fitter = UiCompat.GetComponent<ContentSizeFitter>(child);
                if (fitter != null && childState.ContentSizeFitterEnabled == true)
                {
                    fitter.enabled = false;
                    childState.Touched |= Touched.ContentSizeFitter;
                }
            }

            (applied ??= new HashSet<int>()).Add(child.GetInstanceID());
        }

        state.CounterRotatedChildIds = applied;
    }

    /// <summary>Restores rotation (and, if swapped, size/fitter state) on every child a rule's
    /// counterRotateChildren force-touched.</summary>
    private static void ReleaseCounterRotatedChildren(ElementState state)
    {
        if (state.CounterRotatedChildIds == null)
            return;

        foreach (var id in state.CounterRotatedChildIds)
        {
            if (!_states.TryGetValue(id, out var childState) || childState.Rect == null)
                continue;

            if ((childState.Touched & Touched.Rotation) != 0)
            {
                childState.Touched &= ~Touched.Rotation;
                childState.Rect.localEulerAngles = childState.LocalEuler;
            }

            if ((childState.Touched & Touched.SizeDelta) != 0)
            {
                childState.Touched &= ~Touched.SizeDelta;
                childState.Rect.sizeDelta = childState.SizeDelta;
            }

            if ((childState.Touched & Touched.ContentSizeFitter) != 0 && childState.ContentSizeFitterEnabled.HasValue)
            {
                childState.Touched &= ~Touched.ContentSizeFitter;
                var fitter = UiCompat.GetComponent<ContentSizeFitter>(childState.Rect);
                if (fitter != null)
                    fitter.enabled = childState.ContentSizeFitterEnabled.Value;
            }
        }

        state.CounterRotatedChildIds = null;
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

        ReleaseCounterRotatedChildren(state);

        if (rect == null || touched == Touched.None)
            return;

        _applying = true;
        try
        {
            if ((touched & Touched.AnchorMin) != 0) rect.anchorMin = state.AnchorMin;
            if ((touched & Touched.AnchorMax) != 0) rect.anchorMax = state.AnchorMax;
            if ((touched & Touched.Pivot) != 0) rect.pivot = state.Pivot;
            if ((touched & Touched.SizeDelta) != 0) rect.sizeDelta = state.SizeDelta;
            if ((touched & Touched.AnchoredPosition) != 0) rect.anchoredPosition = state.AnchoredPosition;
            if ((touched & Touched.LocalPosition) != 0) rect.localPosition = state.LocalPosition;
            if ((touched & Touched.LocalScale) != 0) rect.localScale = state.LocalScale;
            if ((touched & Touched.Rotation) != 0) rect.localEulerAngles = state.LocalEuler;
            if ((touched & Touched.SiblingIndex) != 0) rect.SetSiblingIndex(state.SiblingIndex);

            if ((touched & (Touched.ImageEnabled | Touched.PreserveAspect)) != 0)
            {
                var image = UiCompat.GetComponent<Image>(rect);
                if (image != null && (touched & Touched.ImageEnabled) != 0 && state.ImageEnabled.HasValue)
                    image.enabled = state.ImageEnabled.Value;
                if (image != null && (touched & Touched.PreserveAspect) != 0 && state.PreserveAspect.HasValue)
                    image.preserveAspect = state.PreserveAspect.Value;
            }

            if ((touched & Touched.ContentSizeFitter) != 0 && state.ContentSizeFitterEnabled.HasValue)
            {
                var fitter = UiCompat.GetComponent<ContentSizeFitter>(rect);
                if (fitter != null)
                    fitter.enabled = state.ContentSizeFitterEnabled.Value;
            }

            if ((touched & Touched.LayoutGroup) != 0 && state.LayoutGroupEnabled.HasValue)
            {
                var group = UiCompat.GetComponent<LayoutGroup>(rect);
                if (group != null)
                    group.enabled = state.LayoutGroupEnabled.Value;
            }

            if ((touched & (Touched.ContentSizeFitterHorizontal | Touched.ContentSizeFitterVertical)) != 0)
            {
                var fitter = UiCompat.GetComponent<ContentSizeFitter>(rect);
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
                var group = UiCompat.GetComponent<HorizontalOrVerticalLayoutGroup>(rect);
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

    private static void QueueSetActive(GameObject gameObject, bool active)
    {
        _pendingActive[gameObject.GetInstanceID()] = new KeyValuePair<GameObject, bool>(gameObject, active);
    }

    private static void FlushPendingActive()
    {
        if (_pendingActive.Count == 0)
            return;

        var pending = new List<KeyValuePair<GameObject, bool>>(_pendingActive.Values);
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
            _applying = false;
        }
    }

    private static void PruneDestroyed()
    {
        var dead = new List<int>();
        foreach (var pair in _states)
        {
            if (pair.Value.Rect == null)
                dead.Add(pair.Key);
        }

        foreach (var id in dead)
            _states.Remove(id);
    }

    /// <summary>Resolves "../Sibling", "Child/Grandchild", "." relative to an element.</summary>
    internal static Transform ResolveRelative(Transform from, string relativePath)
    {
        var current = from;
        foreach (var segment in relativePath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
                continue;

            current = segment == ".." ? current.parent : current.Find(segment);
            if (current == null)
                return null;
        }

        return current;
    }

    private static float[] Vector(float[] value, int dimensions, LayoutContract contract, string field)
    {
        if (LayoutMath.IsMalformed(value, dimensions))
        {
            WarnOnce($"vector:{contract.Path}:{field}",
                $"[UIEditor] Layout rule '{contract.Path}': {field} needs {dimensions} numbers, got {value.Length}. Ignored.");
            return null;
        }

        return value;
    }

    private static void WarnOnce(string key, string message)
    {
        if (_warned.Add(key))
            _logger?.LogWarning(message);
    }

    private static float[] ToArray(Vector2 value) => new[] { value.x, value.y };
    private static Vector2 ToVector2(float[] value) => new Vector2(value[0], value[1]);
    private static Vector3 ToVector3(float[] value) => new Vector3(value[0], value[1], value[2]);
}

/// <summary>An element's RectTransform values at a point in time.</summary>
internal struct LayoutSnapshot
{
    public Vector2 AnchorMin, AnchorMax, Pivot, AnchoredPosition, SizeDelta;
    public Vector3 LocalPosition, LocalScale, LocalEuler;
}
