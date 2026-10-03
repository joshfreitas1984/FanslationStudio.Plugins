using FanslationStudio.Plugins.Shared;
using Mono.Cecil;
using Mono.Cecil.Cil;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace FanslationStudio.Plugins.DynamicStrings;

/// <summary>
/// Used to replace hardcoded strings in IL
/// </summary>
public class StringDumperService
{
    public static IPluginLogger Logger;
    public static string RegexPattern;
    public static string ManagedPath;
    public static string[] AssemblyPatterns;

    // Built once: the static Regex.IsMatch(string, string) overload re-parses the pattern
    // whenever it falls out of Regex's small static cache.
    private static Regex _matchRegex;

    // Fragments of a called method's full name that mark the string as debug/logging output.
    private static readonly string[] DebugMethodFragments = [
        "Debug", ".Log",
        "ContainsKey", "LitJson", "onError",
        "CustomData", "GetIconSprite"
    ];

    /// <param name="assemblyPatterns">
    /// File globs (relative to <paramref name="managedPath"/>) for the assemblies to scan, separated by ';'.
    /// Games often split their code out of Assembly-CSharp.dll, e.g. "Assembly-CSharp.dll;Mortal.*.dll".
    /// </param>
    public StringDumperService(IPluginLogger logger, string regexPattern, string managedPath, string assemblyPatterns = DefaultAssemblyPatterns)
    {
        Logger = logger;
        RegexPattern = regexPattern;
        _matchRegex = DynamicStringSupport.CreateMatchRegex(regexPattern);
        ManagedPath = managedPath;
        AssemblyPatterns = (assemblyPatterns ?? DefaultAssemblyPatterns)
            .Split([';'], StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .ToArray();
    }

    public const string DefaultAssemblyPatterns = "Assembly-CSharp*.dll";

    /// <summary>Writes dynamicStrings.txt into <paramref name="outputPath"/>. Returns the number of strings dumped.</summary>
    public int DumpFiles(string outputPath)
    {
        Logger.LogWarning("Dumping dynamic strings...");

        try
        {
            var contracts = new List<DynamicStringContract>();

            // Resolve references from the game's own folder rather than wherever BepInEx runs from.
            using var resolver = new DefaultAssemblyResolver();
            resolver.AddSearchDirectory(ManagedPath);
            var readerParameters = new ReaderParameters { AssemblyResolver = resolver };

            foreach (var assemblyPath in FindAssemblies())
            {
                try
                {
                    using var assembly = AssemblyDefinition.ReadAssembly(assemblyPath, readerParameters);
                    int before = contracts.Count;

                    foreach (var module in assembly.Modules)
                        foreach (var type in module.Types)
                            ProcessType(type, contracts);

                    Logger.LogInfo($"{Path.GetFileName(assemblyPath)}: {contracts.Count - before} strings");
                }
                catch (Exception ex)
                {
                    // One unreadable assembly shouldn't cost the rest of the dump.
                    Logger.LogError($"Error reading {assemblyPath}: {ex.Message}");
                }
            }

            var lines = new List<string>();
            foreach (var contract in contracts)
            {
                var parameters = CleanForCsv($"[{string.Join(",", contract.Parameters)}]");
                lines.Add($"{CleanForCsv(contract.Type)},{CleanForCsv(contract.Method)},{CleanForCsv(contract.ILOffset.ToString())},{CleanForCsv(contract.Raw)},{parameters}");
            }

            File.WriteAllLines(Path.Combine(outputPath, DynamicStringContract.FileName), lines);
            Logger.LogWarning($"Dumped to: {outputPath}");
            return lines.Count;
        }
        catch (Exception ex)
        {
            Logger.LogError($"Error dumping: {ex.Message}");
            return 0;
        }

        //SafeFunctions.Sort();
        //UnsafeFunctions.Sort();

        //foreach (var f in SafeFunctions)
        //    Logger.LogWarning($"Safe: {f}");

        //foreach (var f in UnsafeFunctions)
        //    Logger.LogWarning($"Unsafe: {f}");
    }

    private IEnumerable<string> FindAssemblies()
    {
        var paths = AssemblyPatterns
            .SelectMany(pattern => Directory.GetFiles(ManagedPath, pattern))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (paths.Count == 0)
            Logger.LogWarning($"No assemblies in {ManagedPath} match {string.Join(";", AssemblyPatterns)}");

        return paths;
    }

    public string CleanForCsv(string input)
    {
        return input.Replace(",", "，")
            .Replace("\n", "\\n")
            .Replace("\r", "\\r");
    }

    private void ProcessType(TypeDefinition type, List<DynamicStringContract> stringReferences, int recursionLevel = 0)
    {
        // Process nested types. Each is only reachable through its declaring type, so it's
        // visited exactly once.
        if (recursionLevel < 15) //15 should be fine
            foreach (var nestedType in type.NestedTypes)
                ProcessType(nestedType, stringReferences, recursionLevel + 1);

        // Process methods
        foreach (var method in type.Methods)
        {
            if (!method.HasBody)
                continue;

            foreach (var instruction in method.Body.Instructions)
            {
                string operandValue = string.Empty;

                if (instruction.OpCode == OpCodes.Ldstr && instruction.Operand is string stringValue)
                {
                    operandValue = stringValue;
                }
                else if (instruction.OpCode == OpCodes.Ldc_I4
                    && instruction.Operand is int intValue
                    && intValue >= char.MinValue
                    && intValue <= char.MaxValue
                    && IsLikelyCharParameter(instruction))
                {
                    operandValue = $"INVALIDCHAR: {(char)intValue}";
                }

                // Look for string load operations
                if (!string.IsNullOrWhiteSpace(operandValue)
                    && _matchRegex.IsMatch(operandValue))
                {
                    // Skip Debug lines
                    if (IsLikelyDebug(instruction, operandValue))
                        continue;

                    // Add to our list
                    stringReferences.Add(new DynamicStringContract
                    {
                        Type = type.FullName,
                        Method = method.Name,
                        Raw = operandValue,
                        ILOffset = instruction.Offset,
                        Parameters = method.Parameters.Select(p => p.ParameterType.FullName).ToArray(),
                    });
                }
            }
        }
    }

    public static bool IsLikelyDebug(Mono.Cecil.Cil.Instruction currentInstruction, string currentString)
    {
        int instructionCheck = 0;
        var nextInstruction = currentInstruction;

        //Logger.LogError($"For: {currentString}");

        //if (Regex.IsMatch(currentString, @"[a-zA-Z]")
        //    && !currentString.Contains("x", StringComparison.OrdinalIgnoreCase) // For quantity x
        //    && !(currentString.Contains("<") && currentString.Contains(">"))
        //    && !currentString.Contains("size")
        //    && !currentString.Contains("color"))
        //{
        //    //Logger.LogError($"HasEnglish: true");
        //    return true;
        //}

        while (instructionCheck < 5) // check four instructions ahead
        {
            // Get the next instruction after loading the string
            nextInstruction = nextInstruction.Next;

            // Check if there's no next instruction
            if (nextInstruction == null)
                return false;

            //Logger.LogError($"Ref {instructionCheck}: {nextInstruction.OpCode}  {nextInstruction.Operand}");

            //TODO: This still wipes out addresses that happen to have a log straight after a valid function call
            //For now less is more.
            if (nextInstruction.OpCode == OpCodes.Call || nextInstruction.OpCode == OpCodes.Callvirt)
            {
                if (nextInstruction.Operand is MethodReference methodRef)
                {
                    //Logger.LogError($"Ref {instructionCheck}: {methodRef.FullName}");

                    // Cecil rebuilds FullName on every access, so read it once.
                    var fullName = methodRef.FullName;

                    if (fullName.StartsWith("Log", StringComparison.Ordinal))
                        return true;

                    if (ContainsAny(fullName, DebugMethodFragments))
                    {
                        //if (!UnsafeFunctions.Contains(methodRef.FullName))
                        //    UnsafeFunctions.Add(methodRef.FullName);

                        //Logger.LogError($"Ref {instructionCheck}: Debug");
                        return true;
                    }
                    //else if (!SafeFunctions.Contains(methodRef.FullName))
                    //    SafeFunctions.Add(methodRef.FullName);
                }

            }
            else if (nextInstruction.OpCode == OpCodes.Newobj
                || nextInstruction.OpCode == OpCodes.Stloc
                || nextInstruction.OpCode == OpCodes.Ret
                || nextInstruction.OpCode == OpCodes.Rem
                //|| nextInstruction.OpCode == OpCodes.Dup
                || nextInstruction.OpCode == OpCodes.Br
                || nextInstruction.OpCode == OpCodes.Break
                || nextInstruction.OpCode == OpCodes.Brfalse
                || nextInstruction.OpCode == OpCodes.Brfalse_S
                || nextInstruction.OpCode == OpCodes.Brtrue
                || nextInstruction.OpCode == OpCodes.Brtrue_S
                || nextInstruction.OpCode == OpCodes.Br_S)
            {
                //Logger.LogError($"Breaking {instructionCheck}: Not part of Stack {nextInstruction.OpCode}");
                break;
            }

            instructionCheck++;
        }

        return false;
    }

    private static bool ContainsAny(string value, string[] fragments)
    {
        foreach (var fragment in fragments)
            if (value.IndexOf(fragment, StringComparison.Ordinal) >= 0)
                return true;
        return false;
    }

    public static bool IsLikelyCharParameter(Mono.Cecil.Cil.Instruction currentInstruction)
    {
        // Get the next instruction after loading the potential character value
        var nextInstruction = currentInstruction.Next;

        // Check if there's no next instruction
        if (nextInstruction == null)
            return false;

        // Case 1: Direct call to a method that takes a char parameter
        if (nextInstruction.OpCode == OpCodes.Call || nextInstruction.OpCode == OpCodes.Callvirt)
        {
            MethodReference calledMethod = nextInstruction.Operand as MethodReference;
            if (calledMethod != null && calledMethod.Parameters.Count > 0)
            {
                // Check if the first parameter is a char
                // (or if any parameter is a char, depending on your needs)
                foreach (ParameterDefinition param in calledMethod.Parameters)
                {
                    if (param.ParameterType.FullName == "System.Char")
                        return true;
                }
            }
        }

        // Case 2: Character being boxed (common for Split and other methods)
        if (nextInstruction.OpCode == OpCodes.Box &&
            nextInstruction.Operand is TypeReference typeRef &&
            typeRef.FullName == "System.Char")
        {
            return true;
        }

        // Case 3: Looking at a string Split method specifically
        // This might require more context - looking ahead several instructions
        if (IsPartOfStringSplit(currentInstruction))
        {
            return true;
        }

        return false;
    }

    public static bool IsPartOfStringSplit(Mono.Cecil.Cil.Instruction loadInstruction)
    {
        // Look ahead several instructions for a pattern that matches String.Split
        var current = loadInstruction;

        // Skip ahead a few instructions looking for the call
        for (int i = 0; i < 5 && current.Next != null; i++)
        {
            current = current.Next;

            if ((current.OpCode == OpCodes.Call || current.OpCode == OpCodes.Callvirt) &&
                current.Operand is MethodReference methodRef)
            {
                // Check if the method is String.Split
                if (methodRef.DeclaringType.FullName == "System.String" &&
                    methodRef.Name == "Split")
                {
                    return true;
                }
            }
        }

        return false;
    }
}
