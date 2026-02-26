//using BepInEx;
//using BepInEx.Logging;
//using HarmonyLib;
//using System.Collections.Generic;
//using System.IO;
//using System.Reflection;
//using UnityEngine;

//namespace FanslationStudio.Plugins.PrefabText;

//[BepInPlugin($"{MyPluginInfo.PLUGIN_GUID}.TextReplacer", "TextReplacer", MyPluginInfo.PLUGIN_VERSION)]
////[BepInDependency("gravydevsupreme.xunity.resourceredirector")]
//public class TextReplacerPlugin : BaseUnityPlugin
//{
//    internal static new ManualLogSource Logger;
//    internal static Dictionary<string, string> Replacements = [];
//    public static bool Enabled = false;

//    private void Awake()
//    {
//        Logger = base.Logger;

//        if (!Enabled)
//            return;

//        Logger.LogWarning("Loading Prefab Replacements...");
//        var resourcesFolder = Path.Combine(Paths.BepInExRootPath, "resources");
//        var dbFile = $"{resourcesFolder}/dumpedPrefabText.txt";

//        string[] lines = [];

//        if (File.Exists(dbFile))
//            lines = File.ReadAllLines(dbFile);

//        for (int i = 0; i < lines.Length; i += 2)
//        {
//            var raw = lines[i].Replace("- raw: ", "").Replace("\\n", "\n"); //Do not trim some of these have spacing
//            var result = lines[i + 1].Replace("result: ", "").Replace("\\n", "\n");

//            Replacements.Add(raw, result);
//        }

//        Logger.LogWarning("Text Replacer plugin is starting...");
//        //Harmony.CreateAndPatchAll(typeof(PrefabTextPatch));
//        Harmony.CreateAndPatchAll(typeof(TextReplacerPlugin));

//        ResourceRedirection.EnableSyncOverAsyncAssetLoads();

//        ResourceRedirection.RegisterAssetLoadedHook(
//            behaviour: HookBehaviour.OneCallbackPerResourceLoaded,
//            priority: 1001,
//            action: OnAssetLoaded);

//        ResourceRedirection.RegisterResourceLoadedHook(
//            behaviour: HookBehaviour.OneCallbackPerResourceLoaded,
//            priority: 1000,
//            action: OnResourceLoaded);

//        Logger.LogWarning("Text Replacer plugin patching complete!");
//    }

//    private void OnAssetLoaded(AssetLoadedContext context)
//    {
//        foreach (var obj in context.Assets)
//        {
//            ProcessLoadedObject(obj);
//        }
//    }

//    private void OnResourceLoaded(ResourceLoadedContext context)
//    {
//        foreach (var obj in context.Assets)
//        {
//            ProcessLoadedObject(obj);
//        }
//    }

//    private static void ProcessLoadedObject(UnityEngine.Object obj)
//    {
//        // Similar to your existing UpdateText method
//        if (obj is GameObject gameObject)
//        {
//            // For whatever reason IL replacement not working on the switch statement
//            if (gameObject.name.Contains("LoginView"))
//                return;

//            foreach (var component in gameObject.GetComponentsInChildren<Component>(true))
//            {
//                if (component is null)
//                    continue;

//                var type = component.GetType();

//                if (type is null)
//                    continue;

//                var textField =
//                    type.GetField("m_text", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
//                    ?? type.GetField("m_Text", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

//                if (textField != null && textField.FieldType == typeof(string))
//                {
//                    var textValue = textField.GetValue(component) as string;

//                    if (string.IsNullOrEmpty(textValue))
//                        continue;

//                    if (Replacements.ContainsKey(textValue))
//                        textField.SetValue(component, Replacements[textValue]);
//                }
//            }
//        }
//    }
//}