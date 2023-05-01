using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using UnityEditor;
using HarmonyLib;
using ModkitEditorUtils;
using UnityEditor.Compilation;
using UnityEngine;
using Assembly = System.Reflection.Assembly;

[InitializeOnLoad]
public class ModkitPatches
{
    private static void ReplaceCompilerVersionArgument(ref List<string> arguments)
    {
        if(arguments.Count >= 4 && arguments[3].StartsWith("-langversion:"))
            arguments[3] = "-langversion:6";
    }

    private static IEnumerable<CodeInstruction> ReplaceProjectLangVersion(IEnumerable<CodeInstruction> instructions)
    {
        foreach (var inst in instructions)
        {
            if (inst.opcode == OpCodes.Ldstr && (string)inst.operand == "4")
                yield return new CodeInstruction(OpCodes.Ldstr, "6");
            else yield return inst;
        }
    }

    private static void ReplaceApiCompatibilityLevel(ref ApiCompatibilityLevel api_compatibility_level)
    {
        if(ModkitCompiler.ApplyPatch)
            api_compatibility_level = ApiCompatibilityLevel.NET_2_0;
    }

    private static void CompilerLog(CompilerMessage cm)
    {
        if (ModkitCompiler.ApplyPatch)
            Debug.unityLogger.LogFormat(cm.type == CompilerMessageType.Error ? LogType.Error : LogType.Warning,
                "Compiler: {0}", cm.message);
    }

    static ModkitPatches()
    {
        var harmony = new Harmony("qkrisi.modkitpatches");
        var asm = Assembly.GetAssembly(typeof(MonoScript));
        
        var monoCompilerType = asm.GetType("UnityEditor.Scripting.Compilers.MonoCSharpCompiler");
        harmony.Patch(AccessTools.Method(monoCompilerType, "<Compile>m__0"),
            prefix: new HarmonyMethod(typeof(ModkitPatches), nameof(CompilerLog)));
        harmony.Patch(AccessTools.Method(monoCompilerType.BaseType, "StartCompiler", new Type[]
            {
                typeof(BuildTarget),
                typeof(string),
                typeof(List<string>),
                typeof(bool),
                typeof(string)
            }),
            prefix: new HarmonyMethod(AccessTools.Method(typeof(ModkitPatches),
                nameof(ReplaceCompilerVersionArgument))));
        
        var solutionSynchronizerType = asm.GetType("UnityEditor.VisualStudioIntegration.SolutionSynchronizer");
        harmony.Patch(AccessTools.Method(solutionSynchronizerType, "ProjectHeader"),
            transpiler: new HarmonyMethod(typeof(ModkitPatches), nameof(ReplaceProjectLangVersion)));
        
        var monoIslandType = asm.GetType("UnityEditor.Scripting.MonoIsland");
        harmony.Patch(AccessTools.Constructor(monoIslandType,
            new Type[]
            {
                typeof(BuildTarget), typeof(ApiCompatibilityLevel), typeof(string[]), typeof(string[]),
                typeof(string[]), typeof(string)
            }), prefix: new HarmonyMethod(typeof(ModkitPatches), nameof(ReplaceApiCompatibilityLevel)));
    }
}