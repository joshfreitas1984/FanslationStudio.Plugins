using FanslationStudio.Plugins.Shared;
using FanslationStudio.Plugins.Support;
using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace FanslationStudio.Plugins.DynamicStrings;

/// <summary>
/// Used to replace hardcoded strings in IL
/// </summary>
public class StringPatcherService
{
    private readonly Dictionary<string, Type> _cachedTypes = [];

    public static IPluginLogger Logger;
    private static bool _enabled = false;
    private Harmony _harmony;
    private string _bepinExRootPath;
    private string _resourcePath;
    private static IYamlHelper _yamlHelper;

    public StringPatcherService(IPluginLogger logger, bool enabled, 
        Harmony harmony, string resourcePath, string bepinExRootPath, IYamlHelper yamlHelper)
    {
        Logger = logger;
        _enabled = enabled;
        _harmony = harmony;
        _resourcePath = resourcePath;
        _bepinExRootPath = bepinExRootPath;
        _yamlHelper = yamlHelper;
    }

    public void Awake()
    {
        if (!_enabled)
            return;

        Logger.LogMessage("Dynamic String Patcher loading...");

        // Load translations from CSV
        var resourcePath = Path.Combine(_bepinExRootPath, _resourcePath);
        var filePath = Path.Combine(resourcePath, "dynamicStrings.txt");

        if (File.Exists(filePath))
            LoadTranslationsAndApplyPatches(filePath);
        else
            Logger.LogWarning($"Translation file not found at: {resourcePath}");
    }

    public static List<GroupedDynamicStringContracts> GroupedDynamicStringContracts(List<DynamicStringContract> contracts)
    {
        if (contracts == null || contracts.Count == 0)
        {
            return []; // Return an empty list if no contracts are given
        }

        return contracts
            .Where(c => DynamicStringSupport.IsSafeContract(c))
            .GroupBy(c => (c.Type, c.Method, GetParametersKey(c.Parameters)))
            .OrderBy(g => g.Key.Type)
            .ThenBy(g => g.Key.Method)
            .ThenBy(g => g.Key.Item3) // GetParametersKey result
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

    public void LoadTranslationsAndApplyPatches(string filePath)
    {
        Logger.LogMessage($"Loading translations from: {filePath}");

        var badContractErrors = new List<string>();
        
        var lines = File.ReadAllText(filePath);
        var contracts = _yamlHelper.Deserialize<List<DynamicStringContract>>(lines);

        // This is bad because on overloaded functions the addresses will be different
        // we need to match the addresses and the method before grouping
        var groupedContracts = GroupedDynamicStringContracts(contracts);

        Logger.LogMessage("Applying string patches...");

        // Initialize the runtime string interceptor
        //RuntimeStringInterceptor.Initialize(groupedContracts, _harmony);

        int successCount = 0;
        int skipCount = 0;
        int errorCount = 0;

        foreach (var contract in groupedContracts)
        {
            // Get the type
            var targetType = GetTargetType(contract);

            if (targetType == null)
            {
                Logger.LogError($"Could not find type: {contract.Type}");
                skipCount += contract.Contracts.Length;
                continue;
            }

            try
            {
                // Find the method using different approaches based on the method type
                MethodBase targetMethod = null;

                // Special case for static constructors
                if (contract.Method == ".cctor")
                {
                    // Replace static object values that match
                    // Because static constructor would have been called before IL Patch
                    foreach (var fieldInfo in targetType.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                    {
                        var value = fieldInfo.GetValue(null);
                        if (value == null)
                            continue;

                        // If its a string replace it straight out
                        if (value is string originalValue)
                        {
                            string newValue = ReplaceStringIfMatches(originalValue, contract.Contracts);
                            if (originalValue != newValue)
                                fieldInfo.SetValue(null, newValue);
                        }
                        else
                        {
                            ProcessComplexTypeValue(value, contract.Contracts);
                        }
                    }

                    continue; //They need to be patched via fields above because static constructor called before patch                   
                }
                else if (contract.Method == ".ctor")
                {
                    // For instance constructors, try to find the one with matching strings
                    var constructors = AccessTools.GetDeclaredConstructors(targetType)
                        .Where(m => !m.IsStatic)
                        .ToList();

                    foreach (var method in constructors)
                    {
                        if (HasMatchingParameters(contract.Parameters, method))
                        {
                            targetMethod = method;
                            break;
                        }
                    }
                }
                else
                {
                    // Regular methods
                    var methods = AccessTools.GetDeclaredMethods(targetType)
                        .Where(m => m.Name == contract.Method)
                        .ToList();

                    foreach (var method in methods)
                    {
                        if (HasMatchingParameters(contract.Parameters, method))
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

                // Apply the patch
                //_harmony.Patch(targetMethod, transpiler: StringPatcherTranspiler.CreateTranspilerMethod(contract.Contracts, targetMethod));
                _harmony.Patch(targetMethod, transpiler: StringTranspiler.CreateTranspilerMethod(contract.Contracts));

                successCount++;
                Logger.LogDebug($"Successfully patched: {contract.Type}.{contract.Method}");
            }
            catch (Exception ex)
            {
                errorCount++;
                badContractErrors.Add($"Error patching {contract.Type} {contract.Method}\n{ex}");
                //badContractErrors.Add($"\"{contract.Type}.{contract.Method}\",");
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
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                targetType = assembly.GetType(typeContract.Type);
                if (targetType != null)
                    break;
            }
        }

        return targetType;
    }

    private bool HasMatchingParameters(string[] originalParameters, MethodBase methodBase)
    {
        bool match = true;
        var methodParameters = methodBase.GetParameters().Select(p => p.ParameterType).ToArray();
        var methodParameters2 = methodBase.GetParameters().Select(p => p.ParameterType.ToString()).ToArray();

        // Remove empty parameters from deserialization
        originalParameters = originalParameters.Where(o => !string.IsNullOrWhiteSpace(o)).ToArray();

        if (originalParameters.Length == methodParameters.Length)
        {
            for (int i = 0; i < methodParameters.Length; i++)
            {
                // Check if the original parameter matches the method parameter
                if (!IsParameterMatch(originalParameters[i], methodParameters[i]))
                {
                    match = false;
                    break;
                }
            }
        }
        else
            match = false;

        //Logger.LogWarning($"Attempting to match method: {methodBase.Name}");
        //Logger.LogWarning($"Original Parameter Length: {originalParameters.Length} Method: {methodParameters.Length}");
        //Logger.LogWarning($"Original Parameters: {string.Join(", ", originalParameters)}");
        //Logger.LogWarning($"Method Parameters: {string.Join(", ", methodParameters2)}");
        //Logger.LogWarning(match);
        return match;
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
            var originalGenericParts = originalParamType.Split('`');

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

    private string ReplaceStringIfMatches(string value, DynamicStringContract[] contracts)
    {
        if (string.IsNullOrEmpty(value)) return value;

        foreach (var contract in contracts)
        {
            StringTranspiler.PrepareDynamicString(contract.Raw, out string preparedRaw, out string preparedRaw2);
            StringTranspiler.PrepareDynamicString(contract.Translation, out string preparedTrans, out string preparedTrans2);

            if (value == preparedRaw)
            {
                return preparedTrans;
            }
            else if (value == preparedRaw2
                || StringTranspiler.StripCommas(value) == StringTranspiler.StripCommas(preparedRaw))
            {
                return preparedTrans2;
            }
        }

        return value;
    }

    private void ProcessGenericCollection(object collection, DynamicStringContract[] contracts)
    {
        if (collection == null) return;

        Type collectionType = collection.GetType();

        // Handle dictionaries with reflection regardless of key/value types
        if (IsDictionaryType(collectionType))
        {
            ProcessDictionary(collection, contracts);
        }
        // Handle list-like collections
        else if (IsListType(collectionType))
        {
            ProcessList(collection, contracts);
        }
        // Handle other generic types that might contain strings
        else
        {
            // Process properties of the generic object
            foreach (var prop in collectionType.GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                try
                {
                    object propValue = prop.GetValue(collection);
                    if (propValue is string stringValue && prop.CanWrite)
                    {
                        string newValue = ReplaceStringIfMatches(stringValue, contracts);
                        if (stringValue != newValue)
                        {
                            prop.SetValue(collection, newValue);
                        }
                    }
                    else if (propValue != null)
                    {
                        // Recursively process complex property values
                        ProcessComplexTypeValue(propValue, contracts);
                    }
                }
                catch
                {
                    // Skip properties that throw exceptions
                }
            }
        }
    }

    private bool IsDictionaryType(Type type)
    {
        if (type.IsGenericType)
        {
            Type genericTypeDef = type.GetGenericTypeDefinition();
            return genericTypeDef == typeof(Dictionary<,>) ||
                   genericTypeDef == typeof(IDictionary<,>) ||
                   type.GetInterfaces().Any(i => i.IsGenericType &&
                                             i.GetGenericTypeDefinition() == typeof(IDictionary<,>));
        }
        return false;
    }

    private bool IsListType(Type type)
    {
        if (type.IsGenericType)
        {
            Type genericTypeDef = type.GetGenericTypeDefinition();
            return genericTypeDef == typeof(List<>) ||
                   genericTypeDef == typeof(IList<>) ||
                   genericTypeDef == typeof(ICollection<>) ||
                   type.GetInterfaces().Any(i => i.IsGenericType &&
                                            (i.GetGenericTypeDefinition() == typeof(IList<>) ||
                                             i.GetGenericTypeDefinition() == typeof(ICollection<>)));
        }
        return false;
    }

    private void ProcessDictionary(object dictionary, DynamicStringContract[] contracts)
    {
        // Get the generic parameters of the dictionary
        Type dictType = dictionary.GetType();
        Type[] genericArgs = dictType.GetGenericArguments();
        Type keyType = genericArgs[0];
        //Type valueType = genericArgs[1];

        // Get dictionary entries
        var entriesProperty = dictType.GetProperty("Keys");
        var keys = (IEnumerable)entriesProperty.GetValue(dictionary);
        var keysToProcess = new List<object>();

        // Get the keys and check if we need to replace any
        foreach (var key in keys)
        {
            keysToProcess.Add(key);
        }

        // Dynamically invoke item getter and setter
        var itemProperty = dictType.GetProperty("Item");

        // Process each key and value
        foreach (var key in keysToProcess)
        {
            // Process the key if it's a string
            object processedKey = key;
            if (key is string keyString)
            {
                processedKey = ReplaceStringIfMatches(keyString, contracts);
            }

            // Get the value
            object value = itemProperty.GetValue(dictionary, [key]);
            bool valueChanged = false;
            object processedValue = value;

            // Process the value based on its type
            if (value is string valueString)
            {
                string newValue = ReplaceStringIfMatches(valueString, contracts);
                if (newValue != valueString)
                {
                    processedValue = newValue;
                    valueChanged = true;
                }
            }
            else if (value != null)
            {
                // Recursively process the value if it's a complex type
                ProcessComplexTypeValue(value, contracts);
            }

            // If the key changed, we need to remove the old key and add a new entry
            if (!key.Equals(processedKey))
            {
                // Remove old entry
                var removeMethod = dictType.GetMethod("Remove", [keyType]);
                removeMethod.Invoke(dictionary, [key]);

                // Add new entry
                itemProperty.SetValue(dictionary, processedValue, [processedKey]);
            }
            // If only the value changed, just update it
            else if (valueChanged)
            {
                itemProperty.SetValue(dictionary, processedValue, [key]);
            }
        }
    }

    private void ProcessList(object list, DynamicStringContract[] contracts)
    {
        // Get the generic parameter of the list
        Type listType = list.GetType();
        Type[] genericArgs = listType.GetGenericArguments();
        Type elementType = genericArgs[0];

        // If elements are strings, process them directly
        if (elementType == typeof(string))
        {
            var countProperty = listType.GetProperty("Count");
            int count = (int)countProperty.GetValue(list);
            var indexerProperty = listType.GetProperty("Item");

            for (int i = 0; i < count; i++)
            {
                string value = (string)indexerProperty.GetValue(list, [i]);
                string newValue = ReplaceStringIfMatches(value, contracts);

                if (value != newValue)
                {
                    indexerProperty.SetValue(list, newValue, [i]);
                }
            }
        }
        // Otherwise, process each element recursively
        else
        {
            // Get IEnumerator to loop through the list
            var getEnumeratorMethod = listType.GetMethod("GetEnumerator");
            var enumerator = getEnumeratorMethod.Invoke(list, null);
            var enumeratorType = enumerator.GetType();
            var moveNextMethod = enumeratorType.GetMethod("MoveNext");
            var currentProperty = enumeratorType.GetProperty("Current");

            while ((bool)moveNextMethod.Invoke(enumerator, null))
            {
                var element = currentProperty.GetValue(enumerator);
                if (element != null)
                {
                    ProcessComplexTypeValue(element, contracts);
                }
            }
        }
    }

    private void ProcessComplexTypeValue(object value, DynamicStringContract[] contracts)
    {
        Type valueType = value.GetType();

        // Handle arrays
        if (valueType.IsArray)
        {
            Type elementType = valueType.GetElementType();
            Array array = (Array)value;

            if (valueType.GetArrayRank() > 1)
            {
                // Handle multi-dimensional arrays
                int[] lengths = new int[valueType.GetArrayRank()];
                for (int i = 0; i < lengths.Length; i++)
                {
                    lengths[i] = array.GetLength(i);
                }

                foreach (int[] indices in GetAllIndices(lengths))
                {
                    object element = array.GetValue(indices);
                    if (element is string stringValue)
                    {
                        string newValue = ReplaceStringIfMatches(stringValue, contracts);
                        if (stringValue != newValue)
                        {
                            array.SetValue(newValue, indices);
                        }
                    }
                    else if (element != null)
                    {
                        ProcessComplexTypeValue(element, contracts);
                    }
                }
            }
            else if (elementType == typeof(string))
            {
                // Process string array
                for (int i = 0; i < array.Length; i++)
                {
                    string originalValue = (string)array.GetValue(i);
                    string newValue = ReplaceStringIfMatches(originalValue, contracts);

                    if (originalValue != newValue)
                        array.SetValue(newValue, i);
                }
            }
            else
            {
                // Process array of complex types
                for (int i = 0; i < array.Length; i++)
                {
                    object element = array.GetValue(i);
                    if (element != null)
                        ProcessComplexTypeValue(element, contracts);
                }
            }
        }
        // Handle generic collections
        else if (valueType.IsGenericType)
        {
            ProcessGenericCollection(value, contracts);
        }
        // Handle other complex types
        else if (!valueType.IsPrimitive && !valueType.IsEnum)
        {
            // Process regular fields and properties
            foreach (var field in valueType.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                try
                {
                    var fieldValue = field.GetValue(value);
                    if (fieldValue is string stringValue)
                    {
                        string newValue = ReplaceStringIfMatches(stringValue, contracts);
                        if (stringValue != newValue)
                            field.SetValue(value, newValue);
                    }
                    else if (fieldValue != null)
                    {
                        ProcessComplexTypeValue(fieldValue, contracts);
                    }
                }
                catch
                {
                    // Skip fields that throw exceptions
                }
            }

            foreach (var prop in valueType.GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                if (prop.CanRead && prop.CanWrite)
                {
                    try
                    {
                        object propValue = prop.GetValue(value);
                        if (propValue is string stringValue)
                        {
                            string newValue = ReplaceStringIfMatches(stringValue, contracts);
                            if (stringValue != newValue)
                            {
                                prop.SetValue(value, newValue);
                            }
                        }
                        else if (propValue != null)
                        {
                            ProcessComplexTypeValue(propValue, contracts);
                        }
                    }
                    catch
                    {
                        // Skip properties that throw exceptions
                    }
                }
            }
        }
    }

    private IEnumerable<int[]> GetAllIndices(int[] lengths)
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