using UnityEngine;
using UnityEngine.UI;

namespace FanslationStudio.Plugins.UnityShared.Editor;

/// <summary>
/// Draws a tint + outline over the selected element plus a status line, on its own overlay
/// canvas. Nothing here is a raycast target, so it never blocks clicks to the game or the editor.
///
/// The outline is drawn inside the element's bounds and clamped to the screen: many picks are
/// full-screen layers, and an outline drawn outside those would be entirely off-screen.
/// </summary>
internal static class HighlightOverlay
{
    private const float Thickness = 3f;
    private const float StatusHeight = 30f;
    private static readonly Color OutlineColor = new Color(1f, 0.25f, 0.8f, 1f);
    private static readonly Color FillColor = new Color(1f, 0.25f, 0.8f, 0.15f);

    private static GameObject _root;
    private static Canvas _canvas;
    private static RectTransform _canvasRect;
    private static RectTransform _fill;
    private static readonly RectTransform[] _edges = new RectTransform[4];
    private static Text _status;

    private static Vector2 _lastMin;
    private static Vector2 _lastMax;

    // Show runs every frame while anything is selected, so the state of what's on screen is kept
    // here instead of being read back through interop (activeSelf, Text.text) each frame.
    private static bool _visible;
    private static bool _highlightActive;
    private static bool _placed;
    private static string _statusText;

    // The canvas camera only depends on the target, so it's resolved when the target changes.
    private static RectTransform _target;
    private static bool _targetHasCanvas;
    private static Camera _targetCamera;
    private static readonly Vector3[] Corners = new Vector3[4];

    /// <summary>Highlights <paramref name="target"/>, or with a null target shows just the status line.</summary>
    public static void Show(RectTransform target, string statusText)
    {
        EnsureBuilt();
        if (!_visible)
        {
            _root.SetActive(true);
            _visible = true;
        }

        var min = Vector2.zero;
        var max = Vector2.zero;
        var hasBounds = target != null && TryGetScreenBounds(target, out min, out max);
        SetHighlightActive(hasBounds);

        if (hasBounds)
        {
            // Clamp to the screen, then draw the edges inside the (clamped) bounds.
            min = Vector2.Max(min, Vector2.zero);
            max = Vector2.Min(max, new Vector2(Screen.width, Screen.height));

            if (!_placed || min != _lastMin || max != _lastMax)
            {
                _lastMin = min;
                _lastMax = max;
                _placed = true;

                // Canvas has no scaler, so its local units are screen pixels from the bottom-left.
                Place(_fill, min, max);
                Place(_edges[0], new Vector2(min.x, max.y - Thickness), max);                       // top
                Place(_edges[1], min, new Vector2(max.x, min.y + Thickness));                       // bottom
                Place(_edges[2], min, new Vector2(min.x + Thickness, max.y));                       // left
                Place(_edges[3], new Vector2(max.x - Thickness, min.y), max);                       // right
            }
        }

        if (statusText != _statusText)
        {
            _statusText = statusText;
            _status.text = statusText;
        }
    }

    public static void Hide()
    {
        if (_root != null && _visible)
        {
            _root.SetActive(false);
            _visible = false;
        }
    }

    /// <summary>One-line state dump for the log, to diagnose "nothing shows up" reports.</summary>
    public static string Describe()
    {
        if (_root == null)
            return "overlay not built";

        var font = _status != null && _status.font != null ? _status.font.name : "NONE";
        var canvasSize = _canvasRect != null ? _canvasRect.rect.size.ToString() : "?";
        return $"overlay active={_root.activeInHierarchy} canvasEnabled={(_canvas != null && _canvas.isActiveAndEnabled)} " +
               $"canvasSize={canvasSize} screen={Screen.width}x{Screen.height} bounds={_lastMin}-{_lastMax} font={font}";
    }

    private static bool TryGetScreenBounds(RectTransform target, out Vector2 min, out Vector2 max)
    {
        min = max = Vector2.zero;

        if (!ReferenceEquals(target, _target))
        {
            _target = target;
            var canvas = UiCompat.GetComponentInParent<Canvas>(target);
            _targetHasCanvas = canvas != null;
            if (_targetHasCanvas)
            {
                var rootCanvas = canvas.rootCanvas;
                _targetCamera = rootCanvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : rootCanvas.worldCamera;
            }
            else
            {
                _targetCamera = null;
            }
        }

        if (!_targetHasCanvas)
            return false;

        // Axis-aligned bounds of the four corners, so rotated elements are fully enclosed.
        UiCompat.GetWorldCorners(target, Corners);
        min = new Vector2(float.MaxValue, float.MaxValue);
        max = new Vector2(float.MinValue, float.MinValue);
        for (var i = 0; i < Corners.Length; i++)
        {
            var screen = RectTransformUtility.WorldToScreenPoint(_targetCamera, Corners[i]);
            min = Vector2.Min(min, screen);
            max = Vector2.Max(max, screen);
        }

        return max.x > min.x && max.y > min.y;
    }

    private static void Place(RectTransform rect, Vector2 min, Vector2 max)
    {
        rect.anchoredPosition = min;
        rect.sizeDelta = Vector2.Max(max - min, Vector2.zero);
    }

    private static void SetHighlightActive(bool active)
    {
        if (_highlightActive == active)
            return;

        _highlightActive = active;
        _fill.gameObject.SetActive(active);
        foreach (var edge in _edges)
            edge.gameObject.SetActive(active);
    }

    private static void EnsureBuilt()
    {
        if (_root != null)
            return;

        // (Re)building: forget whatever the tracked state said about a previous overlay.
        _visible = true;
        _highlightActive = true;
        _placed = false;
        _statusText = null;
        _target = null;

        _root = new GameObject(ElementPicker.EditorObjectPrefix + "Highlight");
        Object.DontDestroyOnLoad(_root);

        _canvas = UiCompat.AddComponent<Canvas>(_root);
        _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        _canvas.sortingOrder = short.MaxValue - 1; // just below the editor window
        _canvasRect = UiCompat.GetRectTransform(_root);

        // Created in draw order: fill, edges, then the status bar on top of everything.
        var fill = CreateChild<Image>("Fill", out _fill);
        fill.color = FillColor;
        fill.raycastTarget = false;
        AnchorBottomLeft(_fill);

        for (var i = 0; i < _edges.Length; i++)
        {
            var image = CreateChild<Image>("Edge" + i, out _edges[i]);
            image.color = OutlineColor;
            image.raycastTarget = false;
            AnchorBottomLeft(_edges[i]);
        }

        // Status bar pinned to the top of the screen.
        var background = CreateChild<Image>("StatusBackground", out var statusRect);
        background.color = new Color(0f, 0f, 0f, 0.85f);
        background.raycastTarget = false;
        statusRect.anchorMin = new Vector2(0f, 1f);
        statusRect.anchorMax = new Vector2(1f, 1f);
        statusRect.pivot = new Vector2(0.5f, 1f);
        statusRect.sizeDelta = new Vector2(0f, StatusHeight);
        statusRect.anchoredPosition = Vector2.zero;

        var textGo = new GameObject("StatusText");
        textGo.transform.SetParent(statusRect, false);
        _status = UiCompat.AddComponent<Text>(textGo);
        _status.font = UiCompat.GetBuiltinFont();
        _status.fontSize = 16;
        _status.color = Color.white;
        _status.alignment = TextAnchor.MiddleLeft;
        _status.horizontalOverflow = HorizontalWrapMode.Overflow;
        _status.verticalOverflow = VerticalWrapMode.Overflow;
        _status.raycastTarget = false;
        var textRect = UiCompat.GetRectTransform(textGo);
        textRect.anchorMin = Vector2.zero;
        textRect.anchorMax = Vector2.one;
        textRect.offsetMin = new Vector2(10f, 0f);
        textRect.offsetMax = new Vector2(-10f, 0f);
    }

    private static void AnchorBottomLeft(RectTransform rect)
    {
        rect.anchorMin = rect.anchorMax = Vector2.zero;
        rect.pivot = Vector2.zero;
    }

    private static T CreateChild<T>(string name, out RectTransform rect) where T : Component
    {
        var go = new GameObject(name);
        go.transform.SetParent(_canvasRect, false);
        var component = UiCompat.AddComponent<T>(go);
        rect = UiCompat.GetRectTransform(go);
        return component;
    }
}
