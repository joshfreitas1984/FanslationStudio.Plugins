using BepInEx;
using BepInEx.Logging;
using FanslationStudio.Plugins.Sprites;
using FanslationStudio.Plugins.Support;
using FanslationStudio.Plugins.TextResizer;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using TMPro;
using UnityEngine;

namespace FanslationStudio.Plugins.Plugins;

[BepInPlugin($"{MyPluginInfo.PLUGIN_GUID}.TextResizer", "TextResizer", MyPluginInfo.PLUGIN_VERSION)]
internal class TextResizerPlugin : BaseUnityPlugin
{
    private static IPluginLogger _logger;
    private static TextResizerService _service;
    private static bool _enabled = true;

    private KeyCode _addResizerAtCursorHotKey = KeyCode.KeypadMinus;
    private KeyCode _reloadHotkey = KeyCode.KeypadPlus;
    private KeyCode _addResizerHotKey = KeyCode.KeypadMultiply;

    private void Awake()
    {
        _enabled = Config.Bind("General",
            "TextResizerEnabled",
            true,
            "Enable Text Resizer plugin").Value;

        if (!_enabled)
            return;

        _logger = new BepInEx6Logger(base.Logger);
        _service = new TextResizerService(
            _logger, _enabled, Paths.BepInExRootPath);
        _service.Awake();
    }

    internal void Update()
    {
        if (!_enabled)
            return;

        if (UnityInput.Current.GetKeyDown(_reloadHotkey))
            _service.Reload();

        if (UnityInput.Current.GetKeyDown(_addResizerHotKey))
            _service.AddResizersForScene();

        if (UnityInput.Current.GetKeyDown(_addResizerAtCursorHotKey))
            _service.AddResizersAtCursor(UnityInput.Current.mousePosition);
    }
}
