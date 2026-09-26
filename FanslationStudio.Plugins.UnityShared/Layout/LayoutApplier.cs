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
///   * Rules are applied when elements appear (see <see cref="LayoutHooks"/>), on scene change,
///     and on reload. <c>enforce: true</c> rules are also re-applied every tick.
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

    public static bool IsApplied(RectTransform rect)
    {
        return rect != null && _states.TryGetValue(rect.GetInstanceID(), out var state) && state.Applied != null;
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
