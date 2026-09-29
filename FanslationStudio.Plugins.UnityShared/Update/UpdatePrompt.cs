using System;
using BepInEx;
using FanslationStudio.Plugins.UnityShared.Editor;
using FanslationStudio.Plugins.UnityShared.Editor.Ui;
using FanslationStudio.Plugins.Update;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace FanslationStudio.Plugins.UnityShared.Update;

/// <summary>
/// The "update available" window. Plain uGUI, driven by polling like the UI Editor (see
/// <see cref="UiPanel"/>) so it is safe under IL2CPP. Shows only when the background check found a newer
/// patch: offers Update now / Later, shows download progress, then hands over to the installer and quits.
/// </summary>
internal static class UpdatePrompt
{
    private const float WindowWidth = 480f;
    private const float WindowHeight = 190f;
    private const float Padding = 16f;

    private static float _scale = 1f;
    private static GameObject _root;
    private static UiPanel _content;
    private static bool _visible;
    private static bool _dismissed;
    private static bool _applying;
    private static string _builtKey;

    public static void Configure(float scale)
    {
        _scale = Mathf.Clamp(scale, 0.5f, 3f);
    }

    /// <summary>True once the player has chosen Later (or closed an error) for this session.</summary>
    public static bool Dismissed => _dismissed;

    public static void Tick(UpdateService service, string gameDir, DateTime showAfterUtc)
    {
        if (!_visible)
        {
            if (_dismissed || service.Status != UpdateStatus.Available || DateTime.UtcNow < showAfterUtc)
                return;

            Show();
        }

        var key = service.Status + "|" + (service.Progress / 5);
        if (key != _builtKey)
        {
            _builtKey = key;
            Rebuild(service);
        }

        if (service.Status == UpdateStatus.ReadyToApply && !_applying)
        {
            _applying = true;
            // The installer waits for this process to exit before it changes any file.
            if (service.TryLaunchUpdater(System.Diagnostics.Process.GetCurrentProcess().Id, gameDir))
                Application.Quit();
            return;
        }

        var input = UnityInput.Current;
        if (input.GetMouseButtonDown(0))
            _content.HandleClick((Vector2)input.mousePosition);
    }

    private static void Show()
    {
        EnsureBuilt();
        _root.SetActive(true);
        _visible = true;
        _builtKey = null;
    }

    private static void Hide()
    {
        _dismissed = true;
        _visible = false;
        if (_root != null)
            _root.SetActive(false);
    }

    private static void Rebuild(UpdateService service)
    {
        _content.Clear();
        var width = _content.Width;

        switch (service.Status)
        {
            case UpdateStatus.Available:
                _content.Label("Update available", 0, 0, width, 26, 18, TextAnchor.MiddleLeft, null, FontStyle.Bold);
                _content.Label(
                    "A new version of the English patch is ready.\nInstalled: " + service.Installed.Version + "\nLatest:      " + service.LatestVersion,
                    0, 32, width, 66, 13, TextAnchor.UpperLeft);
                _content.Button("Update now", 0, 112, 150, 32, service.StartDownload);
                _content.Button("Later", 160, 112, 110, 32, Hide, UiPanel.MutedButtonColor);
                break;

            case UpdateStatus.Downloading:
                _content.Label("Downloading update... " + service.Progress + "%", 0, 0, width, 26, 16, TextAnchor.MiddleLeft, null, FontStyle.Bold);
                _content.Label("The game will close and restart when the download finishes.", 0, 32, width, 24, 13, TextAnchor.UpperLeft, UiPanel.DimTextColor);
                _content.Box(0, 70, width, 14, new Color(0.25f, 0.25f, 0.3f, 1f));
                _content.Box(0, 70, width * Mathf.Clamp01(service.Progress / 100f), 14, UiPanel.ButtonColor);
                break;

            case UpdateStatus.ReadyToApply:
                _content.Label("Applying update...", 0, 0, width, 26, 16, TextAnchor.MiddleLeft, null, FontStyle.Bold);
                _content.Label("The game is closing. The updater will restart it when it is done.", 0, 32, width, 24, 13, TextAnchor.UpperLeft, UiPanel.DimTextColor);
                break;

            case UpdateStatus.Failed:
                _content.Label("Update failed", 0, 0, width, 26, 16, TextAnchor.MiddleLeft, UiPanel.WarningColor, FontStyle.Bold);
                _content.Label(service.Error ?? "Unknown error.", 0, 32, width, 70, 12, TextAnchor.UpperLeft);
                _content.Button("Close", 0, 112, 110, 32, Hide, UiPanel.MutedButtonColor);
                break;
        }
    }

    private static void EnsureBuilt()
    {
        if (_root != null)
            return;

        _root = new GameObject(ElementPicker.EditorObjectPrefix + "UpdatePrompt");
        Object.DontDestroyOnLoad(_root);

        var canvas = UiCompat.AddComponent<Canvas>(_root);
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = short.MaxValue;
        var scaler = UiCompat.AddComponent<CanvasScaler>(_root);
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
        scaler.scaleFactor = _scale;
        UiCompat.AddComponent<GraphicRaycaster>(_root);
        UiCompat.EnsureEventSystem();

        var window = UiPanel.Create(UiCompat.GetRectTransform(_root), "Window", 0, 0, WindowWidth, WindowHeight,
            new Color(0.12f, 0.08f, 0.2f, 0.97f));
        window.Rect.anchorMin = window.Rect.anchorMax = new Vector2(0.5f, 0.5f);
        window.Rect.pivot = new Vector2(0.5f, 0.5f);
        window.Rect.anchoredPosition = Vector2.zero;

        _content = UiPanel.Create(window.Rect, "Content", Padding, Padding, WindowWidth - 2 * Padding, WindowHeight - 2 * Padding);
    }
}
