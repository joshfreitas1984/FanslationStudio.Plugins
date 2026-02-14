using System;
using UnityEngine;

namespace FanslationStudio.Plugins.Support;
public static class ObjectHelper
{
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
