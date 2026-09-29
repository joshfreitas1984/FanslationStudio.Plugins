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

    public static List<PickedElement> PickAt(Vector2 screenPoint) =>
        ToPickedElements(Collect(screenPoint));

    /// <summary>What every visible element looks like right now, for <see cref="PickChangedSince"/>.</summary>
    public static Dictionary<int, string> SnapshotVisible()
    {
        var snapshot = new Dictionary<int, string>();
        foreach (var hit in Collect(null))
        {
            if (!hit.IsHidden)
                snapshot[hit.RectTransform.GetInstanceID()] = Signature(hit);
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
        var hits = Collect(null);
        hits.RemoveAll(h => h.IsHidden
                            || (snapshot.TryGetValue(h.RectTransform.GetInstanceID(), out var before) && before == Signature(h)));
        return ToPickedElements(hits);
    }

    // Screen bounds (whole pixels, so float noise isn't a change) plus the text.
    private static string Signature(Hit hit)
    {
        var min = new Vector2(float.MaxValue, float.MaxValue);
        var max = new Vector2(float.MinValue, float.MinValue);
        foreach (var corner in UiCompat.GetWorldCorners(hit.RectTransform))
        {
            var screen = RectTransformUtility.WorldToScreenPoint(hit.Camera, corner);
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

    /// <summary>Pickable elements containing <paramref name="screenPoint"/> (or all of them when null), topmost first.</summary>
    private static List<Hit> Collect(Vector2? screenPoint)
    {
        var hits = new List<Hit>();
        // Ancestry lookups are interop calls under IL2CPP; share them between siblings.
        var ancestry = new Dictionary<int, AncestryInfo>();

        foreach (var rectTransform in UiCompat.FindObjectsOfType<RectTransform>())
        {
            if (rectTransform == null || !rectTransform.gameObject.activeInHierarchy)
                continue;

            var info = GetAncestry(rectTransform, ancestry);
            var canvas = info.Canvas;
            if (canvas == null || !canvas.enabled)
                continue;

            // The canvas root is always full-screen; it's never what you meant to pick.
            if (canvas.transform == rectTransform.transform && canvas.isRootCanvas)
                continue;

            var rootCanvas = canvas.rootCanvas;
            if (rootCanvas.name.StartsWith(EditorObjectPrefix))
                continue;

            var rect = rectTransform.rect;
            if (rect.width <= MinPickableSize || rect.height <= MinPickableSize)
                continue;

            if (screenPoint.HasValue)
            {
                var camera = rootCanvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : rootCanvas.worldCamera;
                if (!RectTransformUtility.RectangleContainsScreenPoint(rectTransform, screenPoint.Value, camera))
                    continue;
            }

            hits.Add(new Hit(rectTransform, canvas, rootCanvas, info.Hidden));
        }

        hits.Sort((a, b) => CompareDrawOrder(b, a)); // topmost (drawn last) first
        return hits;
    }

    private static AncestryInfo GetAncestry(Transform transform, Dictionary<int, AncestryInfo> cache)
    {
        if (transform == null)
            return AncestryInfo.None;

        var id = transform.GetInstanceID();
        if (cache.TryGetValue(id, out var cached))
            return cached;

        var parentInfo = GetAncestry(transform.parent, cache);
        var ownCanvas = UiCompat.GetComponent<Canvas>(transform.gameObject);
        var info = new AncestryInfo
        {
            Canvas = ownCanvas != null ? ownCanvas : parentInfo.Canvas,
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
        var result = a.IsOverlay.CompareTo(b.IsOverlay);
        if (result != 0) return result;

        result = a.SortingLayerValue.CompareTo(b.SortingLayerValue);
        if (result != 0) return result;

        result = a.Canvas.sortingOrder.CompareTo(b.Canvas.sortingOrder);
        if (result != 0) return result;

        return CompareSiblingPaths(a.SiblingPath, b.SiblingPath);
    }

    private static int CompareSiblingPaths(List<int> a, List<int> b)
    {
        var shared = a.Count < b.Count ? a.Count : b.Count;
        for (var i = 0; i < shared; i++)
        {
            var result = a[i].CompareTo(b[i]);
            if (result != 0)
                return result;
        }

        // Ancestor is drawn before its descendants.
        return a.Count.CompareTo(b.Count);
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
        public Canvas Canvas;
        public bool Hidden;
    }

    private sealed class Hit
    {
        public RectTransform RectTransform { get; }
        public Canvas Canvas { get; }
        public bool IsOverlay { get; }
        public Camera Camera { get; }
        public int SortingLayerValue { get; }
        public List<int> SiblingPath { get; }
        public bool IsHidden { get; }

        public Hit(RectTransform rectTransform, Canvas canvas, Canvas rootCanvas, bool isHidden)
        {
            RectTransform = rectTransform;
            Canvas = canvas;
            IsOverlay = rootCanvas.renderMode == RenderMode.ScreenSpaceOverlay;
            Camera = IsOverlay ? null : rootCanvas.worldCamera;
            SortingLayerValue = GetSortingLayerValue(canvas);
            IsHidden = isHidden;

            SiblingPath = new List<int>();
            for (var current = rectTransform.transform; current != null; current = current.parent)
                SiblingPath.Insert(0, current.GetSiblingIndex());
        }
    }
}
