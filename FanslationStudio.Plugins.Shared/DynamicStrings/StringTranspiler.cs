using HarmonyLib;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;

namespace FanslationStudio.Plugins.DynamicStrings;

public class StringTranspiler
{
    // Per patched method, keyed by the original so a re-run of the transpiler (Harmony re-runs
    // every transpiler whenever anything else patches the same method) still gets its own
    // translations rather than whichever group was patched last.
    private static readonly Dictionary<MethodBase, MethodTranslations> TranslationsByMethod = [];

    // Undo dump changes
    public static void PrepareDynamicString(string input, out string prepared, out string preparedAlt)
    {
        prepared = input
            .Replace("\\r", "\r")
            .Replace("\\n", "\n");

        // Inconsistent comma use
        preparedAlt = prepared
            .Replace("，", ",");
    }

    public static string StripCommas(string input)
    {
        return input
            .Replace(",", "")
            .Replace("，", "");
    }

    // This method will be used to handle string comparisons in switch statements
    public static bool NewEqualityOperator(string leftComparison, string rightComparison)
    {
        StringPatcherService.Logger.LogFatal($"Testing Equality: [[ {leftComparison} ]] == [[ {rightComparison} ]]");

        // Direct match
        return string.Equals(leftComparison, rightComparison);
    }

    // Harmony passes the original method to any MethodBase parameter of a transpiler.
    public static IEnumerable<CodeInstruction> ReplaceWithTranspiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
    {
        var translations = FindTranslations(__originalMethod);
        if (translations == null)
            return instructions;

        // Map fix remove when fixed in game
        var ifCount = 0;
        bool isMapFix = translations.IsMapFix;
        var rawToTranslated = translations.RawToTranslated;

        var codes = new List<CodeInstruction>(instructions);
        var textReplaced = new List<string>();

        // Step 1: Replace string constants
        for (int i = 0; i < codes.Count; i++)
        {
            //DynamicStringPatcherPlugin.Logger.LogFatal($"Old Instruction: {codes[i].opcode} {codes[i].operand}");

            if (codes[i].opcode == OpCodes.Ldstr &&
                codes[i].operand is string operandStr)
            {
                // Find corresponding translation
                string translatedStr = null;

                if (rawToTranslated.TryGetValue(operandStr, out translatedStr))
                {
                    // Create a new instruction with the translated string but preserve all metadata
                    var newInstruction = new CodeInstruction(OpCodes.Ldstr, translatedStr);
                    MoveLabelsAndBlocks(codes[i], newInstruction);
                    codes[i] = newInstruction;
                    textReplaced.Add(operandStr);
                }
            }

            // Map Fix - Remove when fixed in game
            if (isMapFix)
            {
                // find if (npcPrototype != null)
                if (codes[i].opcode == OpCodes.Ldloc_S
                    && i + 1 < codes.Count
                    && codes[i + 1].opcode == OpCodes.Brfalse)
                {
                    //PatchesPlugin.Logger.LogWarning($"Found if (param != null) at {i}: {codes[i].operand}");

                    if (codes[i].operand.ToString() == "SweetPotato.NpcPrototype (6)")
                    {
                        //PatchesPlugin.Logger.LogWarning($"Found if (npcPrototype != null) at {i}");
                        ifCount++;

                        if (ifCount == 2)
                        {
                            var newInstruction = new CodeInstruction(OpCodes.Ldloc_S, 7); // Load npcPrototype2 (index 7)
                            StringTranspiler.MoveLabelsAndBlocks(codes[i], newInstruction);
                            codes[i] = newInstruction;
                        }


                        if (ifCount == 3)
                        {
                            var newInstruction = new CodeInstruction(OpCodes.Ldloc_S, 8); // Load npcPrototype3 (index 8)
                            StringTranspiler.MoveLabelsAndBlocks(codes[i], newInstruction);
                            codes[i] = newInstruction;
                        }
                    }
                }
            }

        }

        // Add logging to verify the final instructions
        //foreach (var code in codes)
        //    DynamicStringPatcherPlugin.Logger.LogFatal($"New Instruction: {code.opcode} {code.operand}");

        return codes;
    }

    // Every caller replaces rawInstruction with newInstruction, so moving is equivalent to copying.
    // Uses HarmonyX's helpers rather than iterating CodeInstruction.labels ourselves: touching the
    // List<Label> directly makes this assembly reference [netstandard]System.Reflection.Emit.Label,
    // which Unity 2020-era netstandard facades don't forward (TypeLoadException on e.g.
    // LegendOfMortal).
    public static void MoveLabelsAndBlocks(CodeInstruction rawInstruction, CodeInstruction newInstruction)
    {
        newInstruction.MoveLabelsFrom(rawInstruction);
        newInstruction.MoveBlocksFrom(rawInstruction);
    }

    private static MethodTranslations FindTranslations(MethodBase original)
    {
        if (original == null)
            return null;

        if (TranslationsByMethod.TryGetValue(original, out var translations))
            return translations;

        // Fallback in case Harmony hands us a different MethodBase instance for the same method.
        foreach (var entry in TranslationsByMethod)
            if (entry.Key.MethodHandle == original.MethodHandle)
                return entry.Value;

        return null;
    }

    /// <summary>
    /// Registers the contracts to apply to <paramref name="original"/> and returns the transpiler.
    /// Call once per method with every contract targeting it, before patching.
    /// </summary>
    public static HarmonyMethod CreateTranspilerMethod(MethodBase original, IList<DynamicStringContract> contractsToApply)
    {
        // Store the patch data where our transpiler can find it again for this method
        TranslationsByMethod[original] = new MethodTranslations(contractsToApply);

        var methodInfo = typeof(StringTranspiler).GetMethod("ReplaceWithTranspiler",
            BindingFlags.Public | BindingFlags.Static);

        return new HarmonyMethod(methodInfo);
    }

    // Built once per method rather than on every transpiler run.
    private sealed class MethodTranslations
    {
        public Dictionary<string, string> RawToTranslated { get; } = [];
        public bool IsMapFix { get; }

        public MethodTranslations(IList<DynamicStringContract> contracts)
        {
            IsMapFix = contracts.Count > 0 && contracts[0].Type == "MapFuBenDetailPanel" && contracts[0].Method == "Refresh";

            // Build translation dictionaries
            foreach (var replacement in contracts)
            {
                PrepareDynamicString(replacement.Raw, out string preparedRaw, out string preparedRaw2);
                PrepareDynamicString(replacement.Translation, out string preparedTrans, out string preparedTrans2);

                // If we're replacing same string in method
                if (RawToTranslated.ContainsKey(preparedRaw))
                    continue;

                RawToTranslated[preparedRaw] = preparedTrans;
                RawToTranslated[preparedRaw2] = preparedTrans2;
            }
        }
    }
}
