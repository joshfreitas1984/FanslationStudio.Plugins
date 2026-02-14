using HarmonyLib;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;

namespace FanslationStudio.Plugins.DynamicStrings;

public class StringTranspiler
{
    public static DynamicStringContract[] ContractsToApply;

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
        StringPatcherPlugin.Logger.LogFatal($"Testing Equality: [[ {leftComparison} ]] == [[ {rightComparison} ]]");

        // Direct match
        return string.Equals(leftComparison, rightComparison);
    }

    public static IEnumerable<CodeInstruction> ReplaceWithTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        // Map fix remove when fixed in game
        var ifCount = 0;
        bool isMapFix = ContractsToApply[0].Type == "MapFuBenDetailPanel" && ContractsToApply[0].Method == "Refresh";

        var rawToTranslated = new Dictionary<string, string>();

        // Build translation dictionaries
        foreach (var replacement in ContractsToApply)
        {
            PrepareDynamicString(replacement.Raw, out string preparedRaw, out string preparedRaw2);
            PrepareDynamicString(replacement.Translation, out string preparedTrans, out string preparedTrans2);

            // If we're replacing same string in method
            if (rawToTranslated.ContainsKey(preparedRaw))
                continue;

            rawToTranslated[preparedRaw] = preparedTrans;
            rawToTranslated[preparedRaw2] = preparedTrans2;
        }

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
                    CopyLabelsAndBlocks(codes[i], newInstruction);
                    codes[i] = newInstruction;
                    textReplaced.Add(operandStr);
                }
            }

            // Map Fix - Remove when fixed in game
            if (isMapFix)
            {
                // find if (npcPrototype != null)
                if (codes[i].opcode == OpCodes.Ldloc_S
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
                            StringTranspiler.CopyLabelsAndBlocks(codes[i], newInstruction);
                            codes[i] = newInstruction;
                        }


                        if (ifCount == 3)
                        {
                            var newInstruction = new CodeInstruction(OpCodes.Ldloc_S, 8); // Load npcPrototype3 (index 8)
                            StringTranspiler.CopyLabelsAndBlocks(codes[i], newInstruction);
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

    public static void CopyLabelsAndBlocks(CodeInstruction rawInstruction, CodeInstruction newInstruction)
    {
        // Copy labels from original instruction
        if (rawInstruction.labels != null && rawInstruction.labels.Count > 0)
        {
            foreach (var label in rawInstruction.labels)
                newInstruction.labels.Add(label);
        }

        // Copy blocks from original instruction
        if (rawInstruction.blocks != null && rawInstruction.blocks.Count > 0)
        {
            foreach (var block in rawInstruction.blocks)
                newInstruction.blocks.Add(block);
        }
    }

    public static HarmonyMethod CreateTranspilerMethod(DynamicStringContract[] contractsToApply)
    {
        // Store the patch data in a static field so our transpiler can access it
        ContractsToApply = contractsToApply;

        var methodInfo = typeof(StringTranspiler).GetMethod("ReplaceWithTranspiler",
            BindingFlags.Public | BindingFlags.Static);

        return new HarmonyMethod(methodInfo);
    }
}