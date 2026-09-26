using System;
using System.Collections.Generic;
using UnityEngine;

namespace FanslationStudio.Plugins.UnityShared.Editor;

/// <summary>
/// Finds every UI element under a screen point, topmost first.
///
/// Deliberately not an EventSystem/GraphicRaycaster raycast: those only report Graphics with
/// raycastTarget enabled, which skips most labels and every non-Graphic container - exactly the
/// things we want to edit. Instead every active RectTransform is tested geometrically.
/// Elements faded out by a CanvasGroup (alpha 0) are skipped; reach them via parent/child.
/// </summary>
internal static class ElementPicker
{
    /// <summary>GameObjects whose name starts with this are editor UI and are never picked.</summary>
    public const string EditorObjectPrefix = "FSEditor";

    public static List<PickedElement> PickAt(Vector2 screenPoint)
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
            if (canvas == null || !canvas.enabled || info.Hidden)
                continue;

            // The canvas root is always full-screen; it's never what you meant to pick.
            if (canvas.transform == rectTransform.transform && canvas.isRootCanvas)
                continue;

            var rootCanvas = canvas.rootCanvas;
            if (rootCanvas.name.StartsWith(EditorObjectPrefix))
                continue;

            var rect = rectTransform.rect;
            if (rect.width <= 0.5f || rect.height <= 0.5f)
                continue;

            var camera = rootCanvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : rootCanvas.worldCamera;
            if (!RectTransformUtility.RectangleContainsScreenPoint(rectTransform, screenPoint, camera))
                continue;

            hits.Add(new Hit(rectTransform, canvas, rootCanvas));
        }

        hits.Sort((a, b) => CompareDrawOrder(b, a)); // topmost (drawn last) first

        var result = new List<PickedElement>(hits.Count);
        foreach (var hit in hits)
            result.Add(PickedElement.From(hit.RectTransform));
        return result;
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
        public int SortingLayerValue { get; }
        public List<int> SiblingPath { get; }

        public Hit(RectTransform rectTransform, Canvas canvas, Canvas rootCanvas)
        {
            RectTransform = rectTransform;
            Canvas = canvas;
            IsOverlay = rootCanvas.renderMode == RenderMode.ScreenSpaceOverlay;
            SortingLayerValue = GetSortingLayerValue(canvas);

            SiblingPath = new List<int>();
            for (var current = rectTransform.transform; current != null; current = current.parent)
                SiblingPath.Insert(0, current.GetSiblingIndex());
        }
    }
}
