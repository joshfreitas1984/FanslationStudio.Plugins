using FanslationStudio.Plugins.Shared;
using FanslationStudio.Plugins.Support;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace FanslationStudio.Plugins.PrefabText;

/// <summary>
/// Replaces hardcoded strings baked into prefabs/scenes using the translated prefab text files
/// (every file matching the configured pattern, *prefabText* by default).
/// Mono only: this patches Unity's own asset loading APIs with Harmony from Shared, which isn't
/// safe under IL2CPP (see .github/copilot-instructions.md item 4).
/// </summary>
public class PrefabTextReplacerService
{
    private const BindingFlags InstanceFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    public static IPluginLogger Logger;
    private static bool _enabled = false;

    // Harmony postfixes are static, so they reach the live service through this.
    private static PrefabTextReplacerService _instance;

    private readonly Harmony _harmony;
    private readonly string _resourcePath;
    private readonly string _filePattern;
    private readonly string _bepinExRootPath;
    private readonly IYamlHelper _yamlHelper;
    private readonly IPrefabTextFinder _elementFinder;

    private readonly Dictionary<string, string> _replacements = [];
    // Per component type: the serialized text field and (optionally) the public text property.
    // A null entry means the type has no text field and is skipped.
    private readonly Dictionary<Type, TextAccessor> _accessors = [];

    // Deferred until the first frame (see EnsurePatched), matching the other services.
    private bool _patched = false;
    private string[] _pendingFilePaths;

    public PrefabTextReplacerService(IPluginLogger logger, bool enabled,
        Harmony harmony, string resourcePath, string filePattern, string bepinExRootPath, IYamlHelper yamlHelper,
        IPrefabTextFinder elementFinder)
    {
        Logger = logger;
        _enabled = enabled;
        _harmony = harmony;
        _resourcePath = resourcePath;
        _filePattern = filePattern;
        _bepinExRootPath = bepinExRootPath;
        _yamlHelper = yamlHelper;
        _elementFinder = elementFinder;
    }

    public void Awake()
    {
        if (!_enabled)
            return;

        Logger.LogMessage("Prefab Text Replacer loading...");

        var resourcePath = Path.Combine(_bepinExRootPath, _resourcePath);
        var filePaths = TranslationFiles.Find(resourcePath, _filePattern);

        if (filePaths.Length > 0)
            _pendingFilePaths = filePaths;
        else
            Logger.LogWarning($"No prefab text files matching '{_filePattern}' found in: {resourcePath}");
    }

    /// <summary>
    /// Loads the replacements, applies the asset load hooks and replaces text in everything
    /// already loaded. Call from the first frame rather than Awake so the game's own startup
    /// objects exist by the time of the first sweep.
    /// </summary>
    public void EnsurePatched()
    {
        if (_patched || !_enabled || _pendingFilePaths == null)
            return;

        _patched = true;

        try
        {
            foreach (var filePath in _pendingFilePaths)
                LoadReplacements(filePath);
            if (_replacements.Count == 0)
                return;

            _instance = this;
            ApplyPatches();
            SceneManager.sceneLoaded += OnSceneLoaded;
            ReplaceInAllLoadedObjects();
        }
        catch (Exception ex)
        {
            Logger.LogError($"Error starting Prefab Text Replacer: {ex}");
        }
    }

    private void LoadReplacements(string filePath)
    {
        Logger.LogMessage($"Loading prefab text from: {filePath}");

        var contracts = _yamlHelper.Deserialize<List<PrefabTextContract>>(File.ReadAllText(filePath)) ?? [];

        foreach (var contract in contracts)
        {
            if (string.IsNullOrEmpty(contract?.Raw) || string.IsNullOrEmpty(contract.Result))
                continue;

            // Keys are stored as dumped (see PrefabTextDumperService.ExtractTextFromGameObject):
            // newlines escaped as "\n" and carriage returns removed. Do not trim - some of the
            // original strings rely on their spacing.
            _replacements[contract.Raw.Replace("\\n", "\n")] = contract.Result.Replace("\\n", "\n");
        }

        Logger.LogMessage($"{_replacements.Count} prefab text replacements loaded so far");
    }

    private void ApplyPatches()
    {
        var objectPostfix = new HarmonyMethod(typeof(PrefabTextReplacerService), nameof(ObjectResultPostfix));
        var arrayPostfix = new HarmonyMethod(typeof(PrefabTextReplacerService), nameof(ObjectArrayResultPostfix));

        // Generic overloads (Resources.Load<T>, AssetBundle.LoadAsset<T>, ...) all forward to
        // these non-generic ones, so patching these covers them too. Patching the loaded asset
        // (rather than Object.Instantiate) means every instance created from it afterwards is
        // already translated.
        TryPatch(AccessTools.Method(typeof(Resources), nameof(Resources.Load), [typeof(string), typeof(Type)]), objectPostfix);
        TryPatch(AccessTools.PropertyGetter(typeof(ResourceRequest), nameof(ResourceRequest.asset)), objectPostfix);
        TryPatch(AccessTools.Method(typeof(AssetBundle), nameof(AssetBundle.LoadAsset), [typeof(string), typeof(Type)]), objectPostfix);
        TryPatch(AccessTools.Method(typeof(AssetBundle), nameof(AssetBundle.LoadAllAssets), [typeof(Type)]), arrayPostfix);
        TryPatch(AccessTools.PropertyGetter(typeof(AssetBundleRequest), nameof(AssetBundleRequest.asset)), objectPostfix);
        TryPatch(AccessTools.PropertyGetter(typeof(AssetBundleRequest), nameof(AssetBundleRequest.allAssets)), arrayPostfix);
        TryPatch(AccessTools.Method(typeof(Resources), nameof(Resources.LoadAll), [typeof(string), typeof(Type)]), arrayPostfix);

        // The asset hooks above miss prefabs that arrive any other way: referenced from a
        // ScriptableObject/manager loaded later (its dependencies are never passed to us),
        // Addressables/custom loaders, or Unity versions where Resources.Load is extern or gets
        // inlined into Load<T>. Every screen's text components are enabled when it's shown, so
        // catching that covers all of them regardless of where the prefab came from.
        var enablePostfix = new HarmonyMethod(typeof(PrefabTextReplacerService), nameof(TextOnEnablePostfix));
        // Declared-only so a type that doesn't override OnEnable doesn't patch its base twice.
        TryPatch(AccessTools.DeclaredMethod(typeof(TMPro.TextMeshProUGUI), "OnEnable"), enablePostfix);
        TryPatch(AccessTools.DeclaredMethod(typeof(TMPro.TextMeshPro), "OnEnable"), enablePostfix);
        TryPatch(AccessTools.DeclaredMethod(typeof(UnityEngine.UI.Text), "OnEnable"), enablePostfix);

        // Localization components (e.g. Lean Localization's LeanLocalizedTextMeshProUGUI) and
        // other game code assign the original text again after OnEnable - often the prefab's own
        // text kept as a fallback - which would undo the replacement above. Swapping known
        // originals as they're assigned catches that, whoever sets it and whenever.
        var setterPrefix = new HarmonyMethod(typeof(PrefabTextReplacerService), nameof(TextSetterPrefix));
        TryPatch(AccessTools.PropertySetter(typeof(TMPro.TMP_Text), nameof(TMPro.TMP_Text.text)), setterPrefix, isPrefix: true);
        TryPatch(AccessTools.PropertySetter(typeof(UnityEngine.UI.Text), nameof(UnityEngine.UI.Text.text)), setterPrefix, isPrefix: true);
        // Some TMP versions' SetText writes m_text directly instead of going through the setter.
        TryPatch(AccessTools.DeclaredMethod(typeof(TMPro.TMP_Text), "SetText", [typeof(string)]), setterPrefix, isPrefix: true);
        TryPatch(AccessTools.DeclaredMethod(typeof(TMPro.TMP_Text), "SetText", [typeof(string), typeof(bool)]), setterPrefix, isPrefix: true);
    }

    private void TryPatch(MethodBase target, HarmonyMethod patch, bool isPrefix = false)
    {
        // Not every Unity version has every one of these (or they may be extern/inlined), so a
        // missing/unpatchable target only loses that hook rather than the whole plugin.
        if (target == null)
            return;

        try
        {
            if (isPrefix)
                _harmony.Patch(target, prefix: patch);
            else
                _harmony.Patch(target, postfix: patch);
        }
        catch (Exception ex)
        {
            Logger.LogWarning($"Could not patch {target.DeclaringType?.Name}.{target.Name}: {ex.Message}");
        }
    }

    private static void ObjectResultPostfix(UnityEngine.Object __result)
    {
        _instance?.ReplaceInLoadedObject(__result);
    }

    private static void ObjectArrayResultPostfix(UnityEngine.Object[] __result)
    {
        if (__result == null)
            return;

        foreach (var obj in __result)
            _instance?.ReplaceInLoadedObject(obj);
    }

    // Harmony binds the setter's/SetText's first parameter by position, so this works for both
    // "value" (property setter) and "sourceText"/"text" (SetText).
    private static void TextSetterPrefix([HarmonyArgument(0)] ref string value)
    {
        // Runs on every text assignment in the game, so keep it to one lookup and never throw.
        try
        {
            if (_instance == null || string.IsNullOrEmpty(value))
                return;

            var key = value.IndexOf('\r') >= 0 ? value.Replace("\r", "") : value;
            if (_instance._replacements.TryGetValue(key, out var replacement))
                value = replacement;
        }
        catch
        {
            // Leave the game's text as it was.
        }
    }

    private static void TextOnEnablePostfix(Component __instance)
    {
        _instance?.ReplaceInEnabledText(__instance);
    }

    private void ReplaceInEnabledText(Component component)
    {
        // Runs inside the game's own OnEnable, so must never throw back into it.
        try
        {
            if (component != null)
                ReplaceText(component, isLive: true);
        }
        catch (Exception ex)
        {
            Logger.LogError($"Error replacing prefab text in {component?.name}: {ex.Message}");
        }
    }

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        // Covers scene objects and any prefabs loaded as scene dependencies (i.e. referenced
        // from serialized fields rather than loaded through Resources/AssetBundle).
        ReplaceInAllLoadedObjects();
    }

    private void ReplaceInAllLoadedObjects()
    {
        try
        {
            var count = 0;
            foreach (var gameObject in _elementFinder.FindAllGameObjectsInResources())
            {
                // Children are handled via their root's GetComponentsInChildren.
                if (gameObject == null || gameObject.transform.parent != null)
                    continue;

                count += ReplaceInGameObject(gameObject);
            }

            if (count > 0)
                Logger.LogInfo($"Replaced {count} prefab texts in loaded objects");
        }
        catch (Exception ex)
        {
            Logger.LogError($"Error replacing prefab text in loaded objects: {ex.Message}");
        }
    }

    private void ReplaceInLoadedObject(UnityEngine.Object obj)
    {
        // Must never throw back into the game's asset loading call.
        try
        {
            if (obj is GameObject gameObject)
                ReplaceInGameObject(gameObject);
            else if (obj is Component component && component != null)
                ReplaceInGameObject(component.gameObject);
        }
        catch (Exception ex)
        {
            Logger.LogError($"Error replacing prefab text in {obj?.name}: {ex.Message}");
        }
    }

    private int ReplaceInGameObject(GameObject gameObject)
    {
        if (gameObject == null)
            return 0;

        var count = 0;
        // Loaded prefab assets aren't part of any scene; instances living in a scene are.
        var isLive = gameObject.scene.IsValid();

        foreach (var component in _elementFinder.GetComponentsInChildren(gameObject, true))
        {
            if (component != null && ReplaceText(component, isLive))
                count++;
        }

        return count;
    }

    private bool ReplaceText(Component component, bool isLive)
    {
        var accessor = GetAccessor(component.GetType());
        if (accessor == null)
            return false;

        if (accessor.Field.GetValue(component) is not string text || string.IsNullOrEmpty(text))
            return false;

        if (!_replacements.TryGetValue(text.Replace("\r", ""), out var replacement) || replacement == text)
            return false;

        // Live objects have to go through the text property so TMP/UGUI mark themselves dirty
        // and redraw. Prefab assets only need the serialized field changing (instances copy it),
        // and going through the property would register an asset for a canvas rebuild.
        if (isLive && accessor.Property != null)
            accessor.Property.SetValue(component, replacement, null);
        else
            accessor.Field.SetValue(component, replacement);

        return true;
    }

    private TextAccessor GetAccessor(Type type)
    {
        if (_accessors.TryGetValue(type, out var accessor))
            return accessor;

        // Same fields PrefabTextDumperService reads: TMP_Text.m_text and UI.Text.m_Text.
        var field = FindField(type, "m_text") ?? FindField(type, "m_Text");
        if (field != null && field.FieldType == typeof(string))
        {
            var property = type.GetProperty("text", BindingFlags.Public | BindingFlags.Instance);
            if (property == null || property.PropertyType != typeof(string) || !property.CanWrite)
                property = null;

            accessor = new TextAccessor(field, property);
        }

        _accessors[type] = accessor;
        return accessor;
    }

    // Walks the base types explicitly, since GetField doesn't return private fields declared on
    // a base class.
    private static FieldInfo FindField(Type type, string name)
    {
        for (var current = type; current != null; current = current.BaseType)
        {
            var field = current.GetField(name, InstanceFlags | BindingFlags.DeclaredOnly);
            if (field != null)
                return field;
        }

        return null;
    }

    private sealed class TextAccessor(FieldInfo field, PropertyInfo property)
    {
        public FieldInfo Field { get; } = field;
        public PropertyInfo Property { get; } = property;
    }
}
