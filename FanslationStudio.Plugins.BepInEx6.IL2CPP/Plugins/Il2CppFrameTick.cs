using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace FanslationStudio.Plugins.Plugins;

/// <summary>
/// One per-frame tick shared by every IL2CPP plugin. BasePlugin never gets Update(), so the tick
/// is a postfix on the Time.deltaTime getter (see TextResizerPlugin.Load for why that is the safe
/// mechanism). Game code can read deltaTime thousands of times a frame and each read runs the
/// postfix, so there is exactly one patch and one Time.frameCount check per read, however many
/// plugins listen.
/// </summary>
internal static class Il2CppFrameTick
{
    private static readonly List<Action> Listeners = new();
    private static Action[] _snapshot = Array.Empty<Action>();
    private static bool _patched;
    private static int _lastTickedFrame = -1;

    /// <summary>Call from Load(): patching only needs ordinary MethodInfo resolution.</summary>
    public static void Register(string harmonyId, Action tick)
    {
        Listeners.Add(tick);
        _snapshot = Listeners.ToArray();

        if (_patched)
            return;
        _patched = true;

        var harmony = new Harmony(harmonyId + ".FrameTick");
        var deltaTimeGetter = AccessTools.PropertyGetter(typeof(Time), nameof(Time.deltaTime));
        harmony.Patch(deltaTimeGetter, postfix: new HarmonyMethod(typeof(Il2CppFrameTick), nameof(OnDeltaTimeRead)));
    }

    private static void OnDeltaTimeRead()
    {
        var frame = Time.frameCount;
        if (frame == _lastTickedFrame)
            return;
        _lastTickedFrame = frame;

        // Listeners catch and log their own exceptions; this only stops one from skipping the rest.
        var listeners = _snapshot;
        for (var i = 0; i < listeners.Length; i++)
        {
            try
            {
                listeners[i]();
            }
            catch (Exception)
            {
            }
        }
    }
}
