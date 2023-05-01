using System.IO;
using UnityEditor;
using UnityEngine;

namespace ModkitEditorUtils
{
    public static class ModkitCompiler
    {
        internal static bool ApplyPatch;

        public static bool CompileAssembly(string[] sources, string[] references, string[] defines, string outputFile, bool applyPatch = true)
        {
            ApplyPatch = applyPatch;
            EditorUtility.CompileCSharp(sources, references, defines, outputFile);
            ApplyPatch = false;
            if (!File.Exists(outputFile))
            {
                Debug.LogError("Compilation failed!");
                return false;
            }
            File.Delete(outputFile + ".mdb");
            Debug.Log("Compilation complete!");
            return true;
        }
    }
}