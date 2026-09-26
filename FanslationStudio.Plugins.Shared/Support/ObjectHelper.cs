using System;
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

    // Helper method to get the full path of a GameObject in the hierarchy
    public static string GetGameObjectPath(GameObject obj)
    {
        string path = obj.name;
        Transform parent = obj.transform.parent;

        while (parent != null)
        {
            path = parent.name + "/" + path;
            parent = parent.parent;
        }

        return path;
    }
}
