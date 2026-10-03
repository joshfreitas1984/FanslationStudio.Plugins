using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace FanslationStudio.Plugins.Support;
public static class ObjectHelper
{
    /// <summary>
    /// GameObjects whose name starts with this are the UI Editor's own overlay, never the game's
    /// UI - appliers and resizers must skip them. Defined here (rather than alongside
    /// ElementPicker, which owns the concept) because Shared is a separate assembly that appliers
    /// living in Shared, like TextResizerService, can't otherwise see; ElementPicker.EditorObjectPrefix
    /// is kept in sync with this value.
    /// </summary>
    public const string EditorObjectPrefix = "FSEditor";

    public static string GetObjectPath(this object obj)
    {
        if (obj is not GameObject && obj is not Component)
            throw new ArgumentException("Expected object to be a GameObject or component.", "obj");

        var asset = obj is GameObject gameObject ? gameObject : ((Component)obj).gameObject;
        return GetGameObjectPath(asset);
    }

    // Reused by GetGameObjectPath: building the path back to front with string concatenation
    // copies the whole tail at every level (O(depth^2)), and it runs on hot text/sprite hooks.
    [ThreadStatic] private static List<string> _pathNames;
    [ThreadStatic] private static StringBuilder _pathBuilder;

    // Helper method to get the full path of a GameObject in the hierarchy
    public static string GetGameObjectPath(GameObject obj)
    {
        var names = _pathNames ??= new List<string>(16);
        var builder = _pathBuilder ??= new StringBuilder(128);
        names.Clear();
        builder.Length = 0;

        names.Add(obj.name);
        for (var parent = obj.transform.parent; parent != null; parent = parent.parent)
            names.Add(parent.name);

        for (var i = names.Count - 1; i >= 0; i--)
        {
            builder.Append(names[i]);
            if (i > 0)
                builder.Append('/');
        }

        names.Clear();
        return builder.ToString();
    }

    /// <summary>True if the path belongs to the UI Editor's own overlay (ordinal, so it's cheap on hot paths).</summary>
    public static bool IsEditorObjectPath(string path)
    {
        return path != null && path.StartsWith(EditorObjectPrefix, StringComparison.Ordinal);
    }
}
