using System;
using System.Collections.Generic;
using FanslationStudio.Plugins.Support;
using UnityEngine;

namespace FanslationStudio.Plugins.UnityShared.Editor;

/// <summary>
/// Finds every UI element under a screen point, topmost first.
///
/// Deliberately not an EventSystem/GraphicRaycaster raycast: those only report Graphics with
/// raycastTarget enabled, which skips most labels and every non-Graphic container - exactly the
/// things we want to edit. Instead every active RectTransform is tested geometrically.
/// Elements faded out by a CanvasGroup (alpha 0) are still returned, flagged via
/// <see cref="PickedElement.IsHidden"/>, so they can be picked without hunting through parent/child.
/// </summary>
internal static class ElementPicker
{
    /// <summary>GameObjects whose name starts with this are editor UI and are never picked.</summary>
    public const string EditorObjectPrefix = ObjectHelper.EditorObjectPrefix;

    // Filters out degenerate (unlaid-out or genuinely zero-sized) rects without excluding small
    // but real elements; the old 0.5f threshold was large enough to hide legitimate small labels.
    private const float MinPickableSize = 0.01f;

    private static readonly Vector3[] Corners = new Vector3[4];
    private static readonly List<int> SiblingScratch = new List<int>(32);

    public static List<PickedElement> PickAt(Vector2 screenPoint) =>
        ToPickedElements(Collect(screenPoint, sort: true));

    /// <summary>What every visible element looks like right now, for <see cref="PickChangedSince"/>.</summary>
    public static Dictionary<int, string> SnapshotVisible()
    {
        var snapshot = new Dictionary<int, string>();
        // Order doesn't matter for a lookup, so no sort.
        foreach (var hit in Collect(null, sort: false))
        {
            if (!hit.IsHidden)
                snapshot[hit.InstanceId] = Signature(hit);
        }
        return snapshot;
    }

    /// <summary>
    /// Every visible element that is new since <paramref name="snapshot"/>, or has moved, resized,
    /// faded in or changed its text, topmost first. Finds tooltips and popups wherever they are
    /// drawn (they rarely sit under the cursor), including reused ones the game keeps active and
    /// just fills in and moves into place.
    /// </summary>
    public static List<PickedElement> PickChangedSince(Dictionary<int, string> snapshot)
    {
        // Filter first, then sort only what's left (usually a handful of elements).
        var changed = new List<Hit>();
        foreach (var hit in Collect(null, sort: false))
        {
            if (hit.IsHidden)
                continue;
            if (snapshot.TryGetValue(hit.InstanceId, out var before) && before == Signature(hit))
                continue;
            changed.Add(hit);
        }

        SortTopmostFirst(changed);
        return ToPickedElements(changed);
    }

    // Screen bounds (whole pixels, so float noise isn't a change) plus the text.
    private static string Signature(Hit hit)
    {
        var min = new Vector2(float.MaxValue, float.MaxValue);
        var max = new Vector2(float.MinValue, float.MinValue);
        UiCompat.GetWorldCorners(hit.RectTransform, Corners);
        for (var i = 0; i < Corners.Length; i++)
        {
            var screen = RectTransformUtility.WorldToScreenPoint(hit.Canvas.Camera, Corners[i]);
            min = Vector2.Min(min, screen);
            max = Vector2.Max(max, screen);
        }

        return $"{Mathf.RoundToInt(min.x)},{Mathf.RoundToInt(min.y)},{Mathf.RoundToInt(max.x)},{Mathf.RoundToInt(max.y)}|" +
               PickedElement.ReadText(hit.RectTransform);
    }

    private static List<PickedElement> ToPickedElements(List<Hit> hits)
    {
        var result = new List<PickedElement>(hits.Count);
        foreach (var hit in hits)
            result.Add(PickedElement.From(hit.RectTransform, hit.IsHidden));
        return result;
    }

    /// <summary>Pickable elements containing <paramref name="screenPoint"/> (or all of them when null),
    /// topmost first if <paramref name="sort"/> is set.</summary>
    private static List<Hit> Collect(Vector2? screenPoint, bool sort)
    {
        var hits = new List<Hit>();
        // Ancestry lookups are interop calls under IL2CPP; share them between siblings. Each
        // canvas's facts (enabled, root, camera, sorting) are read once, when its transform is.
        var ancestry = new Dictionary<int, AncestryInfo>();

        foreach (var rectTransform in UiCompat.FindObjectsOfType<RectTransform>())
        {
            if (rectTransform == null || !rectTransform.gameObject.activeInHierarchy)
                continue;

            var id = rectTransform.GetInstanceID();
            var info = GetAncestry(rectTransform, id, ancestry);
            var canvas = info.Canvas;
            if (canvas == null || !canvas.Enabled || canvas.IsEditor)
                continue;

            // The canvas root is always full-screen; it's never what you meant to pick.
            if (canvas.TransformId == id && canvas.IsRootCanvas)
                continue;

            var rect = rectTransform.rect;
            if (rect.width <= MinPickableSize || rect.height <= MinPickableSize)
                continue;

            if (screenPoint.HasValue && !RectTransformUtility.RectangleContainsScreenPoint(rectTransform, screenPoint.Value, canvas.Camera))
                continue;

            hits.Add(new Hit { RectTransform = rectTransform, InstanceId = id, Canvas = canvas, IsHidden = info.Hidden });
        }

        if (sort)
            SortTopmostFirst(hits);
        return hits;
    }

    private static void SortTopmostFirst(List<Hit> hits)
    {
        if (hits.Count < 2)
            return;

        foreach (var hit in hits)
            hit.SiblingPath = BuildSiblingPath(hit.RectTransform);
        hits.Sort((a, b) => CompareDrawOrder(b, a)); // topmost (drawn last) first
    }

    // Root first. Walks up once into a scratch list rather than inserting at the front per level.
    private static int[] BuildSiblingPath(Transform transform)
    {
        SiblingScratch.Clear();
        for (var current = transform; current != null; current = current.parent)
            SiblingScratch.Add(current.GetSiblingIndex());

        var path = new int[SiblingScratch.Count];
        for (var i = 0; i < path.Length; i++)
            path[i] = SiblingScratch[path.Length - 1 - i];
        return path;
    }

    private static AncestryInfo GetAncestry(Transform transform, int id, Dictionary<int, AncestryInfo> cache)
    {
        if (cache.TryGetValue(id, out var cached))
            return cached;

        var parent = transform.parent;
        var parentInfo = parent == null ? AncestryInfo.None : GetAncestry(parent, parent.GetInstanceID(), cache);
        var ownCanvas = UiCompat.GetComponent<Canvas>(transform.gameObject);
        var info = new AncestryInfo
        {
            Canvas = ownCanvas != null ? CanvasInfo.Read(ownCanvas, id) : parentInfo.Canvas,
            Hidden = parentInfo.Hidden,
        };

        var group = UiCompat.GetComponent<CanvasGroup>(transform.gameObject);
        if (group != null && group.enabled)
        {
            if (group.ignoreParentGroups)
                info.Hidden = false;
            if (group.alpha <= 0.001f)
                info.Hidden = true;
        }

        cache[id] = info;
        return info;
    }

    // Approximates uGUI draw order: overlay canvases above camera/world canvases, then sorting
    // layer and order of the nearest canvas, then hierarchy order (depth-first; children after
    // their parent, later siblings after earlier ones).
    private static int CompareDrawOrder(Hit a, Hit b)
    {
        var result = a.Canvas.IsOverlay.CompareTo(b.Canvas.IsOverlay);
        if (result != 0) return result;

        result = a.Canvas.SortingLayerValue.CompareTo(b.Canvas.SortingLayerValue);
        if (result != 0) return result;

        result = a.Canvas.SortingOrder.CompareTo(b.Canvas.SortingOrder);
        if (result != 0) return result;

        return CompareSiblingPaths(a.SiblingPath, b.SiblingPath);
    }

    private static int CompareSiblingPaths(int[] a, int[] b)
    {
        var shared = a.Length < b.Length ? a.Length : b.Length;
        for (var i = 0; i < shared; i++)
        {
            var result = a[i].CompareTo(b[i]);
            if (result != 0)
                return result;
        }

        // Ancestor is drawn before its descendants.
        return a.Length.CompareTo(b.Length);
    }

    private static int GetSortingLayerValue(Canvas canvas)
    {
        try
        {
            return SortingLayer.GetLayerValueFromID(canvas.sortingLayerID);
        }
        catch (Exception)
        {
            // Can be stripped from some IL2CPP builds; sorting order still gives a good answer.
            return 0;
        }
    }

    private struct AncestryInfo
    {
        public static readonly AncestryInfo None = default;
        public CanvasInfo Canvas;
        public bool Hidden;
    }

    /// <summary>A canvas's facts, read once per collect instead of once per element under it.</summary>
    private sealed class CanvasInfo
    {
        public int TransformId;
        public bool Enabled;
        public bool IsRootCanvas;
        public bool IsEditor;
        public bool IsOverlay;
        public Camera Camera;
        public int SortingLayerValue;
        public int SortingOrder;

        public static CanvasInfo Read(Canvas canvas, int transformId)
        {
            var rootCanvas = canvas.rootCanvas;
            var isOverlay = rootCanvas.renderMode == RenderMode.ScreenSpaceOverlay;
            return new CanvasInfo
            {
                TransformId = transformId,
                Enabled = canvas.enabled,
                IsRootCanvas = canvas.isRootCanvas,
                IsEditor = rootCanvas.name.StartsWith(EditorObjectPrefix, StringComparison.Ordinal),
                IsOverlay = isOverlay,
                Camera = isOverlay ? null : rootCanvas.worldCamera,
                SortingLayerValue = GetSortingLayerValue(canvas),
                SortingOrder = canvas.sortingOrder,
            };
        }
    }

    private sealed class Hit
    {
        public RectTransform RectTransform;
        public int InstanceId;
        public CanvasInfo Canvas;
        public bool IsHidden;
        // Only built for hits that get sorted.
        public int[] SiblingPath;
    }
}
