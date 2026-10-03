using FanslationStudio.Plugins.Shared;
using FanslationStudio.Plugins.Support;
using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace FanslationStudio.Plugins.DynamicStrings;

/// <summary>
/// Used to replace hardcoded strings in IL
/// </summary>
public class StringPatcherService
{
    private readonly Dictionary<string, Type> _cachedTypes = [];
    // Many groups target methods of the same type, so look each type's members up once.
    private readonly Dictionary<Type, List<MethodInfo>> _cachedMethods = [];
    private readonly Dictionary<Type, List<ConstructorInfo>> _cachedConstructors = [];
    // Shared by every .cctor group's static field rewrite.
    private readonly Dictionary<Type, ValueShape> _valueShapes = [];

    public static IPluginLogger Logger;
    private static bool _enabled = false;
    private Harmony _harmony;
    private string _bepinExRootPath;
    private string _resourcePath;
    private string _filePattern;
    private static IYamlHelper _yamlHelper;

    // Tracks whether patches have been applied yet. Applying is deferred (see EnsurePatched)
    // rather than done immediately in Awake/Load, because under IL2CPP, Harmony resolves
    // Il2CppType tokens for patch parameter types via Il2CppType.From. If this runs before
    // Unity has naturally initialized the relevant modules (e.g. TMPro), it can force their
    // static cctor to run reentrantly inside Il2CppInterop's generic-method hook, corrupting
    // memory (AccessViolationException) and crashing the game.
    private bool _patched = false;
    private string[] _pendingFilePaths;

    public StringPatcherService(IPluginLogger logger, bool enabled,
        Harmony harmony, string resourcePath, string filePattern, string bepinExRootPath, IYamlHelper yamlHelper)
    {
        Logger = logger;
        _enabled = enabled;
        _harmony = harmony;
        _resourcePath = resourcePath;
        _filePattern = filePattern;
        _bepinExRootPath = bepinExRootPath;
        _yamlHelper = yamlHelper;
    }

    public void Awake()
    {
        if (!_enabled)
            return;

        Logger.LogMessage("Dynamic String Patcher loading...");

        var resourcePath = Path.Combine(_bepinExRootPath, _resourcePath);
        var filePaths = TranslationFiles.Find(resourcePath, _filePattern);

        if (filePaths.Length > 0)
            _pendingFilePaths = filePaths;
        else
            Logger.LogWarning($"No translation files matching '{_filePattern}' found in: {resourcePath}");
    }

    /// <summary>
    /// Applies the Harmony patches. Must be called after at least one frame/scene has run
    /// (e.g. from the plugin's Update, not from Awake/Load) so Unity has had a chance to
    /// naturally initialize the relevant modules before Harmony/Il2CppInterop tries to
    /// resolve their type tokens - doing this too early can crash the game under IL2CPP.
    /// </summary>
    public void EnsurePatched()
    {
        if (_patched || !_enabled || _pendingFilePaths == null)
            return;

        _patched = true;
        LoadTranslationsAndApplyPatches(_pendingFilePaths);
    }

    public static List<GroupedDynamicStringContracts> GroupedDynamicStringContracts(List<DynamicStringContract> contracts)
    {
        if (contracts == null || contracts.Count == 0)
        {
            return []; // Return an empty list if no contracts are given
        }

        // Ordinal: the order only affects logging, so there's no need for culture-aware sorting.
        return contracts
            .Where(c => DynamicStringSupport.IsSafeContract(c))
            .GroupBy(c => (c.Type, c.Method, GetParametersKey(c.Parameters)))
            .OrderBy(g => g.Key.Type, StringComparer.Ordinal)
            .ThenBy(g => g.Key.Method, StringComparer.Ordinal)
            .ThenBy(g => g.Key.Item3, StringComparer.Ordinal) // GetParametersKey result
            .Select(group => new GroupedDynamicStringContracts
            {
                Type = group.Key.Type,
                Method = group.Key.Method,
                Parameters = group.First().Parameters,
                Contracts = [.. group]
            })
            .ToList();
    }

    public static string GetParametersKey(string[] parameters)
    {
        return string.Join(",", parameters);
    }

    public void LoadTranslationsAndApplyPatches(string[] filePaths)
    {
        var badContractErrors = new List<string>();

        var contracts = new List<DynamicStringContract>();
        foreach (var filePath in filePaths)
        {
            Logger.LogMessage($"Loading translations from: {filePath}");
            var fileContracts = _yamlHelper.Deserialize<List<DynamicStringContract>>(File.ReadAllText(filePath));
            if (fileContracts != null)
                contracts.AddRange(fileContracts);
        }

        // This is bad because on overloaded functions the addresses will be different
        // we need to match the addresses and the method before grouping
        var groupedContracts = GroupedDynamicStringContracts(contracts);

        Logger.LogMessage("Applying string patches...");

        // Initialize the runtime string interceptor
        //RuntimeStringInterceptor.Initialize(groupedContracts, _harmony);

        int successCount = 0;
        int skipCount = 0;
        int errorCount = 0;

        // Groups can resolve to the same method (e.g. parameter names that match more than one
        // overload loosely), so collect every method's contracts first and patch each once.
        var pendingPatches = new List<PendingPatch>();
        var pendingByMethod = new Dictionary<MethodBase, PendingPatch>();

        foreach (var contract in groupedContracts)
        {
            try
            {
                // Get the type. Inside the try so one type that fails to load only costs its own group.
                var targetType = GetTargetType(contract);

                if (targetType == null)
                {
                    Logger.LogError($"Could not find type: {contract.Type}");
                    skipCount += contract.Contracts.Length;
                    continue;
                }

                // Find the method using different approaches based on the method type
                MethodBase targetMethod = null;

                // Special case for static constructors
                if (contract.Method == ".cctor")
                {
                    // Replace static object values that match
                    // Because static constructor would have been called before IL Patch
                    ReplaceInStaticFields(targetType, contract.Contracts);
                    continue; //They need to be patched via fields above because static constructor called before patch
                }

                // Remove empty parameters from deserialization
                var expectedParameters = (contract.Parameters ?? []).Where(o => !string.IsNullOrWhiteSpace(o)).ToArray();

                if (contract.Method == ".ctor")
                {
                    // For instance constructors, try to find the one with matching strings
                    foreach (var method in GetConstructors(targetType))
                    {
                        if (HasMatchingParameters(expectedParameters, method))
                        {
                            targetMethod = method;
                            break;
                        }
                    }
                }
                else
                {
                    // Regular methods
                    foreach (var method in GetMethods(targetType))
                    {
                        if (method.Name == contract.Method && HasMatchingParameters(expectedParameters, method))
                        {
                            targetMethod = method;
                            break;
                        }
                    }
                }

                if (targetMethod == null)
                {
                    Logger.LogError($"Could not find method: {contract.Type}.{contract.Method}");
                    skipCount++;
                    continue;
                }

                if (!pendingByMethod.TryGetValue(targetMethod, out var pending))
                {
                    pending = new PendingPatch(targetMethod, contract.Type, contract.Method);
                    pendingByMethod[targetMethod] = pending;
                    pendingPatches.Add(pending);
                }

                pending.Contracts.AddRange(contract.Contracts);
                pending.GroupCount++;
            }
            catch (Exception ex)
            {
                errorCount++;
                badContractErrors.Add($"Error patching {contract.Type} {contract.Method}\n{ex}");
                //badContractErrors.Add($"\"{contract.Type}.{contract.Method}\",");
            }
        }

        foreach (var pending in pendingPatches)
        {
            try
            {
                // Apply the patch
                //_harmony.Patch(targetMethod, transpiler: StringPatcherTranspiler.CreateTranspilerMethod(contract.Contracts, targetMethod));
                _harmony.Patch(pending.Method, transpiler: StringTranspiler.CreateTranspilerMethod(pending.Method, pending.Contracts));

                successCount += pending.GroupCount;
                Logger.LogDebug($"Successfully patched: {pending.Type}.{pending.Name}");
            }
            catch (Exception ex)
            {
                errorCount += pending.GroupCount;
                badContractErrors.Add($"Error patching {pending.Type} {pending.Name}\n{ex}");
            }
        }

        Logger.LogWarning($"Patching summary: {successCount} successful, {skipCount} skipped, {errorCount} errors");

        //Batch errors until the end
        foreach (var error in badContractErrors)
            Logger.LogError(error);
    }

    private Type GetTargetType(GroupedDynamicStringContracts typeContract)
    {
        if (!_cachedTypes.TryGetValue(typeContract.Type, out var targetType))
        {
            // The dump uses Cecil names, which separate nested types with '/'; reflection uses '+'.
            var reflectionName = typeContract.Type.Replace('/', '+');
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                targetType = assembly.GetType(reflectionName);
                if (targetType != null)
                    break;
            }

            _cachedTypes[typeContract.Type] = targetType;
        }

        return targetType;
    }

    private List<MethodInfo> GetMethods(Type type)
    {
        if (!_cachedMethods.TryGetValue(type, out var methods))
        {
            methods = AccessTools.GetDeclaredMethods(type);
            _cachedMethods[type] = methods;
        }

        return methods;
    }

    private List<ConstructorInfo> GetConstructors(Type type)
    {
        if (!_cachedConstructors.TryGetValue(type, out var constructors))
        {
            constructors = AccessTools.GetDeclaredConstructors(type)
                .Where(m => !m.IsStatic)
                .ToList();
            _cachedConstructors[type] = constructors;
        }

        return constructors;
    }

    // expectedParameters must already have the empty entries from deserialization removed.
    private bool HasMatchingParameters(string[] expectedParameters, MethodBase methodBase)
    {
        var methodParameters = methodBase.GetParameters();

        if (expectedParameters.Length != methodParameters.Length)
            return false;

        for (int i = 0; i < methodParameters.Length; i++)
        {
            // Check if the original parameter matches the method parameter
            if (!IsParameterMatch(expectedParameters[i], methodParameters[i].ParameterType))
                return false;
        }

        return true;
    }

    private bool IsParameterMatch(string originalParamType, Type methodParamType)
    {
        // Direct full name match
        if (originalParamType == methodParamType.FullName)
            return true;

        // Match simple name
        if (originalParamType == methodParamType.Name)
            return true;

        // Handle generic types
        if (methodParamType.IsGenericType)
        {
            // Compare base generic type name
            var genericTypeName = methodParamType.GetGenericTypeDefinition().Name;
            if (originalParamType.Contains(genericTypeName))
                return true;

            // Check generic type arguments
            var genericArgs = methodParamType.GetGenericArguments();
            var originalGenericParts = originalParamType.Split(new[] { '`' });

            if (originalGenericParts.Length > 1)
            {
                // Check if base type matches and number of generic arguments match
                if (originalGenericParts[0] == genericTypeName &&
                    genericArgs.Length == int.Parse(originalGenericParts[1]))
                    return true;
            }
        }

        // Additional fallback for partial matches
        return originalParamType.EndsWith(methodParamType.Name);
    }

    private void ReplaceInStaticFields(Type targetType, DynamicStringContract[] contracts)
    {
        var rewriter = new StaticValueRewriter(new StringReplacementLookup(contracts), _valueShapes);

        foreach (var fieldInfo in targetType.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
        {
            // Constants can't be set (and were inlined into their callers anyway).
            if (fieldInfo.IsLiteral)
                continue;

            var value = fieldInfo.GetValue(null);
            if (value == null)
                continue;

            // If its a string replace it straight out
            if (value is string originalValue)
            {
                string newValue = rewriter.Lookup.Replace(originalValue);
                if (originalValue != newValue)
                    fieldInfo.SetValue(null, newValue);
            }
            else
            {
                rewriter.Visit(value, 0);
            }
        }
    }

    private sealed class PendingPatch(MethodBase method, string type, string name)
    {
        public MethodBase Method { get; } = method;
        public string Type { get; } = type;
        public string Name { get; } = name;
        public List<DynamicStringContract> Contracts { get; } = [];
        public int GroupCount { get; set; }
    }

    /// <summary>
    /// A group's raw -> translation lookup for the static field rewrite, built once per group
    /// rather than re-preparing every contract for every string visited. Matches exactly as a
    /// linear scan of the contracts would: the first contract whose prepared raw, comma-normalised
    /// raw or comma-stripped raw matches wins, giving its translation when the prepared raw itself
    /// matched and its comma-normalised translation otherwise.
    /// </summary>
    private sealed class StringReplacementLookup
    {
        private readonly string[] _raw;
        private readonly string[] _translation;
        private readonly string[] _translationAlt;
        // Each maps to the index of the first contract with that key.
        private readonly Dictionary<string, int> _byRaw = [];
        private readonly Dictionary<string, int> _byRawAlt = [];
        private readonly Dictionary<string, int> _byStrippedRaw = [];

        public StringReplacementLookup(DynamicStringContract[] contracts)
        {
            _raw = new string[contracts.Length];
            _translation = new string[contracts.Length];
            _translationAlt = new string[contracts.Length];

            for (var i = 0; i < contracts.Length; i++)
            {
                var contract = contracts[i];
                if (contract?.Raw == null || contract.Translation == null)
                    continue;

                StringTranspiler.PrepareDynamicString(contract.Raw, out string preparedRaw, out string preparedRaw2);
                StringTranspiler.PrepareDynamicString(contract.Translation, out string preparedTrans, out string preparedTrans2);

                _raw[i] = preparedRaw;
                _translation[i] = preparedTrans;
                _translationAlt[i] = preparedTrans2;

                AddFirst(_byRaw, preparedRaw, i);
                AddFirst(_byRawAlt, preparedRaw2, i);
                AddFirst(_byStrippedRaw, StringTranspiler.StripCommas(preparedRaw), i);
            }
        }

        private static void AddFirst(Dictionary<string, int> lookup, string key, int index)
        {
            if (!lookup.ContainsKey(key))
                lookup[key] = index;
        }

        public string Replace(string value)
        {
            if (string.IsNullOrEmpty(value)) return value;

            var index = int.MaxValue;
            if (_byRaw.TryGetValue(value, out var i))
                index = i;
            if (_byRawAlt.TryGetValue(value, out i) && i < index)
                index = i;

            var stripped = value.IndexOf(',') >= 0 || value.IndexOf('，') >= 0 ? StringTranspiler.StripCommas(value) : value;
            if (_byStrippedRaw.TryGetValue(stripped, out i) && i < index)
                index = i;

            if (index == int.MaxValue)
                return value;

            return value == _raw[index] ? _translation[index] : _translationAlt[index];
        }
    }

    private enum ValueKind
    {
        // Nothing worth walking: primitives, enums, strings (handled by the parent), Unity
        // objects, delegates and reflection objects.
        Skip,
        Array,
        Dictionary,
        List,
        // Any other collection: only its elements' contents can be changed.
        Collection,
        Fields,
    }

    private sealed class ValueShape(ValueKind kind, FieldInfo[] fields = null)
    {
        public ValueKind Kind { get; } = kind;
        public FieldInfo[] Fields { get; } = fields;
    }

    /// <summary>
    /// Walks the object graph under a static field, replacing matching strings in place. Only
    /// fields are walked - never property getters, which can have side effects (e.g. Unity's
    /// MeshFilter.mesh clones the mesh); auto-properties are covered by their backing fields.
    /// Each object is visited once (so cycles terminate) and the depth is capped.
    /// </summary>
    private sealed class StaticValueRewriter(StringReplacementLookup lookup, Dictionary<Type, ValueShape> shapes)
    {
        private const int MaxDepth = 32;
        private const BindingFlags InstanceFields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        private readonly HashSet<object> _visited = new(ReferenceComparer.Instance);

        public StringReplacementLookup Lookup { get; } = lookup;

        public void Visit(object value, int depth)
        {
            if (value == null || depth > MaxDepth)
                return;

            var type = value.GetType();
            var shape = GetShape(type);
            if (shape.Kind == ValueKind.Skip)
                return;

            // Boxed value types are fresh copies, so only reference types can repeat.
            if (!type.IsValueType && !_visited.Add(value))
                return;

            switch (shape.Kind)
            {
                case ValueKind.Array:
                    VisitArray((Array)value, depth);
                    break;
                case ValueKind.Dictionary:
                    VisitDictionary((IDictionary)value, depth);
                    break;
                case ValueKind.List:
                    VisitList((IList)value, depth);
                    break;
                case ValueKind.Collection:
                    VisitCollection((IEnumerable)value, depth);
                    break;
                case ValueKind.Fields:
                    VisitFields(value, shape.Fields, depth);
                    break;
            }
        }

        private void VisitArray(Array array, int depth)
        {
            if (array.Length == 0)
                return;

            if (array.Rank > 1)
            {
                // Handle multi-dimensional arrays
                int[] lengths = new int[array.Rank];
                for (int i = 0; i < lengths.Length; i++)
                    lengths[i] = array.GetLength(i);

                foreach (int[] indices in GetAllIndices(lengths))
                    VisitArrayElement(array, array.GetValue(indices), indices, depth);
            }
            else
            {
                for (int i = 0; i < array.Length; i++)
                    VisitArrayElement(array, array.GetValue(i), [i], depth);
            }
        }

        private void VisitArrayElement(Array array, object element, int[] indices, int depth)
        {
            if (element is string stringValue)
            {
                string newValue = Lookup.Replace(stringValue);
                if (stringValue != newValue)
                    array.SetValue(newValue, indices);
            }
            else if (element != null)
            {
                Visit(element, depth + 1);
            }
        }

        private void VisitDictionary(IDictionary dictionary, int depth)
        {
            // Copy the keys first, since entries may be replaced below.
            var keys = new List<object>();
            foreach (var key in dictionary.Keys)
                keys.Add(key);

            foreach (var key in keys)
            {
                try
                {
                    // Process the key if it's a string
                    object processedKey = key is string keyString ? Lookup.Replace(keyString) : key;

                    object value = dictionary[key];
                    object processedValue = value;
                    bool valueChanged = false;

                    if (value is string valueString)
                    {
                        string newValue = Lookup.Replace(valueString);
                        if (newValue != valueString)
                        {
                            processedValue = newValue;
                            valueChanged = true;
                        }
                    }
                    else if (value != null)
                    {
                        // Recursively process the value if it's a complex type
                        Visit(value, depth + 1);
                    }

                    // If the key changed, we need to remove the old key and add a new entry
                    if (!key.Equals(processedKey))
                    {
                        dictionary.Remove(key);
                        dictionary[processedKey] = processedValue;
                    }
                    // If only the value changed, just update it
                    else if (valueChanged)
                    {
                        dictionary[key] = processedValue;
                    }
                }
                catch
                {
                    // Skip entries that can't be read or written (e.g. read-only dictionaries)
                }
            }
        }

        private void VisitList(IList list, int depth)
        {
            for (int i = 0; i < list.Count; i++)
            {
                try
                {
                    var element = list[i];
                    if (element is string stringValue)
                    {
                        string newValue = Lookup.Replace(stringValue);
                        if (stringValue != newValue && !list.IsReadOnly)
                            list[i] = newValue;
                    }
                    else if (element != null)
                    {
                        Visit(element, depth + 1);
                    }
                }
                catch
                {
                    // Skip elements that can't be read or written
                }
            }
        }

        private void VisitCollection(IEnumerable collection, int depth)
        {
            try
            {
                foreach (var element in collection)
                {
                    if (element != null && element is not string)
                        Visit(element, depth + 1);
                }
            }
            catch
            {
                // Skip collections that can't be enumerated
            }
        }

        private void VisitFields(object value, FieldInfo[] fields, int depth)
        {
            foreach (var field in fields)
            {
                try
                {
                    var fieldValue = field.GetValue(value);
                    if (fieldValue is string stringValue)
                    {
                        string newValue = Lookup.Replace(stringValue);
                        if (stringValue != newValue)
                            field.SetValue(value, newValue);
                    }
                    else if (fieldValue != null)
                    {
                        Visit(fieldValue, depth + 1);
                    }
                }
                catch
                {
                    // Skip fields that throw exceptions
                }
            }
        }

        private ValueShape GetShape(Type type)
        {
            if (!shapes.TryGetValue(type, out var shape))
            {
                shape = CreateShape(type);
                shapes[type] = shape;
            }

            return shape;
        }

        private static ValueShape CreateShape(Type type)
        {
            if (IsLeaf(type)
                || typeof(Delegate).IsAssignableFrom(type)
                || typeof(MemberInfo).IsAssignableFrom(type)
                || typeof(Assembly).IsAssignableFrom(type)
                || typeof(Module).IsAssignableFrom(type)
                || IsUnityObject(type))
                return new ValueShape(ValueKind.Skip);

            if (type.IsArray)
                return new ValueShape(IsLeaf(type.GetElementType()) && type.GetElementType() != typeof(string) ? ValueKind.Skip : ValueKind.Array);

            if (typeof(IDictionary).IsAssignableFrom(type))
                return new ValueShape(ValueKind.Dictionary);

            if (typeof(IList).IsAssignableFrom(type))
                return new ValueShape(ValueKind.List);

            if (typeof(ICollection).IsAssignableFrom(type) || IsGenericCollection(type))
                return new ValueShape(ValueKind.Collection);

            // Walk the base types explicitly, since GetFields doesn't return private fields
            // declared on a base class (e.g. an inherited auto-property's backing field).
            var fields = new List<FieldInfo>();
            for (var current = type; current != null && current != typeof(object) && current != typeof(ValueType); current = current.BaseType)
            {
                foreach (var field in current.GetFields(InstanceFields))
                {
                    if (field.FieldType == typeof(string) || !IsLeaf(field.FieldType))
                        fields.Add(field);
                }
            }

            return new ValueShape(fields.Count > 0 ? ValueKind.Fields : ValueKind.Skip, [.. fields]);
        }

        private static bool IsLeaf(Type type)
        {
            return type.IsPrimitive || type.IsEnum || type.IsPointer || type == typeof(string) || type == typeof(decimal);
        }

        private static bool IsGenericCollection(Type type)
        {
            foreach (var i in type.GetInterfaces())
            {
                if (i.IsGenericType && i.GetGenericTypeDefinition() == typeof(ICollection<>))
                    return true;
            }

            return false;
        }

        // By name so Shared never touches Unity's own types (see IPrefabTextFinder).
        private static bool IsUnityObject(Type type)
        {
            for (var current = type; current != null; current = current.BaseType)
            {
                if (current.FullName == "UnityEngine.Object")
                    return true;
            }

            return false;
        }

        private static IEnumerable<int[]> GetAllIndices(int[] lengths)
        {
            int[] indices = new int[lengths.Length];
            while (true)
            {
                yield return (int[])indices.Clone();

                for (int i = lengths.Length - 1; i >= 0; i--)
                {
                    if (indices[i] < lengths[i] - 1)
                    {
                        indices[i]++;
                        break;
                    }
                    else
                    {
                        if (i == 0)
                            yield break;
                        indices[i] = 0;
                    }
                }
            }
        }
    }

    // Reference identity, so objects that override Equals/GetHashCode are still each visited once.
    private sealed class ReferenceComparer : IEqualityComparer<object>
    {
        public static readonly ReferenceComparer Instance = new();

        public new bool Equals(object x, object y) => ReferenceEquals(x, y);

        public int GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
    }
}
