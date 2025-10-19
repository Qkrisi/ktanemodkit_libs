using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Reflection;
using System.Collections.Generic;
using Mono.Cecil;
using Mono.Cecil.Cil;
using UnityEditor;
using Debug = UnityEngine.Debug;
using FieldAttributes = Mono.Cecil.FieldAttributes;
using OpCode = Mono.Cecil.Cil.OpCode;
using TypeAttributes = Mono.Cecil.TypeAttributes;
using MethodAttributes = Mono.Cecil.MethodAttributes;
using MethodImplAttributes = Mono.Cecil.MethodImplAttributes;
using OperandType = Mono.Cecil.Cil.OperandType;
using ParameterAttributes = Mono.Cecil.ParameterAttributes;

namespace ModkitEditorUtils
{
    public static class ModkitCompiler
    {
        private static readonly OpCode Pop;
        private static readonly OpCode Ldstr;
        private static readonly OpCode Ldc_I4_0;
        private static readonly OpCode Ldc_I4;
        private static readonly OpCode Ldc_I4_S;
        private static readonly OpCode Newarr;
        private static readonly OpCode Dup;
        private static readonly OpCode Stfld;
        private static readonly OpCode Stelem_Ref;
        private static readonly OpCode Call;
        private static readonly OpCode Ldnull;
        private static readonly OpCode Ldarg_0;
        private static readonly OpCode Ldarg_1;
        private static readonly OpCode Ret;
        
        private static readonly MethodInfo simplifyMacrosMethod;
        private static readonly MethodInfo optimizeMacrosMethod;
        
        private static readonly Type ArrayTypeInternal;
        
        private static readonly ConstructorInfo AttributeUsageConstructor;
        
        internal static bool ApplyPatch;

        private const string DebugType = "KModkit.Internal.DebugInfo";

        public static bool CompileAssembly(string[] sources, string[] references, string[] defines, string outputFile, bool debug, bool applyPatch = true)
        {
            ApplyPatch = applyPatch;
            EditorUtility.CompileCSharp(sources, references, defines, outputFile);
            ApplyPatch = false;
            if (!File.Exists(outputFile))
            {
                Debug.LogError("Compilation failed!");
                return false;
            }

            if (debug && File.Exists(outputFile + ".mdb"))
                ApplyDebugPatch(outputFile, outputFile, true);
            File.Delete(outputFile + ".mdb");
            
            Debug.Log("Compilation complete!");
            return true;
        }

        private static IEnumerable<MethodDefinition> GetTypeMethods(TypeDefinition type) =>
            type.Methods.Concat(type.NestedTypes.SelectMany(GetTypeMethods));

        public static void ApplyDebugPatch(string file, string output, bool trimPath)
        {
            WaitForAccess(file);
            using (var asmDef = AssemblyDefinition.ReadAssembly(file, new ReaderParameters
                   {
                       ReadWrite = true,
                       ReadSymbols = true
                   }))
            {
                var module = asmDef.MainModule;
                
                int maxOffset = 0;

                var stringArrayType =
                    (TypeReference)Activator.CreateInstance(ArrayTypeInternal,
                        new object[] { module.TypeSystem.String });
                
                var debugInfoType = new TypeDefinition("KModkit.Internal", "DebugInfo",
                    TypeAttributes.NotPublic | TypeAttributes.Sealed | TypeAttributes.Abstract |
                    TypeAttributes.BeforeFieldInit, module.TypeSystem.Object);
                module.Types.Add(debugInfoType);

                var debugLinesAttribute = new TypeDefinition("KModkit.Internal", "DebugLinesAttribute",
                    TypeAttributes.NotPublic | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit,
                    module.ImportReference(typeof(Attribute)));
                var debugLinesAttributeConstructor = new MethodDefinition(".ctor", MethodAttributes.Assembly | MethodAttributes.HideBySig | MethodAttributes.SpecialName |
                    MethodAttributes.RTSpecialName, module.TypeSystem.Void);
                var debugLinesField =
                    new FieldDefinition("debugLines", FieldAttributes.Private, stringArrayType);
                debugLinesAttributeConstructor.Parameters.Add(new ParameterDefinition("_debugLines",
                    ParameterAttributes.None, stringArrayType));
                debugLinesAttribute.Fields.Add(debugLinesField);
                debugLinesAttribute.Methods.Add(debugLinesAttributeConstructor);
                var debugLinesConstructorProcessor = debugLinesAttributeConstructor.Body.GetILProcessor();
                debugLinesConstructorProcessor.Append(debugLinesConstructorProcessor.Create(Ldarg_0));
                debugLinesConstructorProcessor.Append(debugLinesConstructorProcessor.Create(Ldarg_1));
                debugLinesConstructorProcessor.Append(debugLinesConstructorProcessor.Create(Stfld, debugLinesField));
                debugLinesConstructorProcessor.Append(debugLinesConstructorProcessor.Create(Ret));
                var attributeTargetsTypeRef = module.ImportReference(typeof(AttributeTargets));
                var attributeUsageCtor = module.ImportReference(AttributeUsageConstructor);
                var attributeUsageAttrib = new CustomAttribute(attributeUsageCtor);
                attributeUsageAttrib.ConstructorArguments.Add(new CustomAttributeArgument(attributeTargetsTypeRef, (int)AttributeTargets.Method));
                debugLinesAttribute.CustomAttributes.Add(attributeUsageAttrib);
                
                module.Types.Add(debugLinesAttribute);

                MethodDefinition sourceDocumentsMethod = CreateStringArrayFunc(module, "SourceDocuments", stringArrayType);
                MethodDefinition debugLinesMethod = CreateStringArrayFunc(module, "DebugLines", stringArrayType);
                MethodDefinition debugLineMethod = new MethodDefinition("_DebugLine",
                    MethodAttributes.Assembly | MethodAttributes.Static | MethodAttributes.HideBySig, module.TypeSystem.Void);
                debugLineMethod.ImplAttributes |= MethodImplAttributes.NoInlining;
                debugLineMethod.Parameters.Add(new ParameterDefinition("_debugLine", ParameterAttributes.None,
                    module.TypeSystem.String));
                var debugLineProcessor = debugLineMethod.Body.GetILProcessor();
                debugLineProcessor.Append(debugLineProcessor.Create(Ret));
                debugInfoType.Methods.Add(sourceDocumentsMethod);
                debugInfoType.Methods.Add(debugLinesMethod);
                debugInfoType.Methods.Add(debugLineMethod);
                var methods = new HashSet<MethodDefinition>(module.Types.SelectMany(GetTypeMethods));
                var documentDict = new Dictionary<string, int>();
                var debugLines = new HashSet<string>();
                foreach (var method in methods
                             .Where(m => m.HasBody && m.DebugInformation.HasSequencePoints))
                { 

                    if (method.DeclaringType == debugInfoType || method.DeclaringType == debugLinesAttribute)
                        continue;
                    
                    var processor = method.Body.GetILProcessor();
                    int offsetIndex = -1;
                    var methodDebugLines = new List<string>();
                    simplifyMacrosMethod.Invoke(null, new object[] { method.Body });
                    foreach (var sp in method.DebugInformation.SequencePoints.Where(sp => !sp.IsHidden)
                                 .OrderBy(sp => sp.Offset))
                    {
                        offsetIndex++;
                        if (offsetIndex > maxOffset)
                            maxOffset = offsetIndex;
                        var instruction = method.Body.Instructions.FirstOrDefault(inst => inst.Offset == sp.Offset);
                        if (instruction is null)
                            continue;
                        var path = sp.Document.Url;
                        if (trimPath)
                        {
                            var assetsIndex = path.IndexOf(Path.DirectorySeparatorChar + "Assets");
                            if (assetsIndex > -1)
                                path = path.Substring(assetsIndex + 1);
                        }

                        if (!documentDict.TryGetValue(path, out var documentIndex))
                        {
                            documentIndex = documentDict.Count;
                            documentDict.Add(path, documentIndex);
                        }

                        var debugLine = $"_ktane_debugline_{sp.Offset}_{documentIndex}_{sp.StartLine}_{sp.StartColumn}";
                        debugLines.Add(debugLine);
                        methodDebugLines.Add(debugLine);
                        var moved = CloneInstruction(processor, instruction);
                        instruction.OpCode = Ldstr;

                        instruction.Operand = debugLine;
                        processor.InsertAfter(instruction, moved);
                        processor.InsertAfter(instruction, processor.Create(Call, debugLineMethod));
                    }
                    optimizeMacrosMethod.Invoke(null, new object[] { method.Body });
                    
                    var attribute = new CustomAttribute(debugLinesAttributeConstructor);
                    attribute.ConstructorArguments.Add(new CustomAttributeArgument(stringArrayType,
                        methodDebugLines.Select(line => new CustomAttributeArgument(module.TypeSystem.String, line))
                            .ToArray()));
                    method.CustomAttributes.Add(attribute);

                    method.ImplAttributes |= MethodImplAttributes.NoInlining;
                }

                foreach (var inst in sourceDocumentsMethod.Body.Instructions)
                {
                    if (inst.OpCode == Ldc_I4_0 || (inst.OpCode == Ldc_I4 && (int)inst.Operand == 0) ||
                        (inst.OpCode == Ldc_I4_S && (sbyte)inst.Operand == 0))
                    {
                        inst.OpCode = Ldc_I4;
                        inst.Operand = documentDict.Count;
                    }

                    if (inst.OpCode == Newarr)
                    {
                        var processor = sourceDocumentsMethod.Body.GetILProcessor();
                        var currentInstruction = inst;
                        foreach (var sourceFile in documentDict.OrderBy(pair => pair.Value))
                        {
                            var dup = processor.Create(Dup);
                            var loadIndex = processor.Create(Ldc_I4, sourceFile.Value);
                            var loadFile = processor.Create(Ldstr, sourceFile.Key);
                            var storeElement = processor.Create(Stelem_Ref);
                            processor.InsertAfter(currentInstruction, dup);
                            processor.InsertAfter(dup, loadIndex);
                            processor.InsertAfter(loadIndex, loadFile);
                            processor.InsertAfter(loadFile, storeElement);
                            currentInstruction = storeElement;
                        }
                        break;
                    }
                }
                
                foreach (var inst in debugLinesMethod.Body.Instructions)
                {
                    if (inst.OpCode == Ldc_I4_0 || (inst.OpCode == Ldc_I4 && (int)inst.Operand == 0) ||
                        (inst.OpCode == Ldc_I4_S && (sbyte)inst.Operand == 0))
                    {
                        inst.OpCode = Ldc_I4;
                        inst.Operand = debugLines.Count;
                    }

                    if (inst.OpCode == Newarr)
                    {
                        var processor = debugLinesMethod.Body.GetILProcessor();
                        var currentInstruction = inst;
                        var allLines = debugLines.ToArray();
                        for(int i = 0; i<allLines.Length; i++)
                        {
                            var dup = processor.Create(Dup);
                            var loadIndex = processor.Create(Ldc_I4, i);
                            var loadFile = processor.Create(Ldstr, allLines[i]);
                            var storeElement = processor.Create(Stelem_Ref);
                            processor.InsertAfter(currentInstruction, dup);
                            processor.InsertAfter(dup, loadIndex);
                            processor.InsertAfter(loadIndex, loadFile);
                            processor.InsertAfter(loadFile, storeElement);
                            currentInstruction = storeElement;
                        }
                        break;
                    }
                }
                
                asmDef.Write(output+".patched");
            }
            WaitForAccess(output);
            if(File.Exists(output))
                File.Delete(output);
            File.Move(output+".patched", output);
        }

        private static MethodDefinition CreateStringArrayFunc(ModuleDefinition module, string name, TypeReference stringArrayType)
        {
            var method = new MethodDefinition(name, MethodAttributes.Assembly | MethodAttributes.Static | MethodAttributes.HideBySig, stringArrayType);
            var il = method.Body.GetILProcessor();
            il.Append(il.Create(Ldc_I4_0));
            il.Append(il.Create(Newarr, module.TypeSystem.String));
            il.Append(il.Create(Ret));
            return method;
        }

        private static Instruction CloneInstruction(ILProcessor processor, Instruction instruction)
        {
            if (instruction.OpCode.OperandType == OperandType.InlineNone)
                return processor.Create(instruction.OpCode);
            if(instruction.Operand is TypeReference typeRef)
                return processor.Create(instruction.OpCode, typeRef);
            if(instruction.Operand is CallSite callSite)
                return processor.Create(instruction.OpCode, callSite);
            if(instruction.Operand is MethodReference methodRef)
                return processor.Create(instruction.OpCode, methodRef);
            if(instruction.Operand is FieldReference fieldRef)
                return processor.Create(instruction.OpCode, fieldRef);
            if(instruction.Operand is string str)
                return processor.Create(instruction.OpCode, str);
            if(instruction.Operand is sbyte sb)
                return processor.Create(instruction.OpCode, sb);
            if(instruction.Operand is byte b)
                return processor.Create(instruction.OpCode, b);
            if(instruction.Operand is int i)
                return processor.Create(instruction.OpCode, i);
            if(instruction.Operand is long l)
                return processor.Create(instruction.OpCode, l);
            if(instruction.Operand is float f)
                return processor.Create(instruction.OpCode, f);
            if(instruction.Operand is double d)
                return processor.Create(instruction.OpCode, d);
            if(instruction.Operand is Instruction ins)
                return processor.Create(instruction.OpCode, ins);
            if(instruction.Operand is Instruction[] instructions)
                return processor.Create(instruction.OpCode, instructions);
            if(instruction.Operand is VariableDefinition variable)
                return processor.Create(instruction.OpCode, variable);
            if(instruction.Operand is ParameterDefinition parameter)
                return processor.Create(instruction.OpCode, parameter);
            throw new ArgumentException($"Unknown operand: {instruction.Operand}");
        }

        private static void WaitForAccess(string file)
        {
            while(new FileInfo(file).Length == 0)
                Thread.Sleep(50);
            for (int i = 0; i < 100; i++)
            {
                try
                {
                    using (var stream = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
                        return;
                }
                catch (IOException)
                {
                    Thread.Sleep(50);
                }
            }
            throw new IOException("Failed to open assembly to patch");
        }

        private static OpCode GetOpCode(Type opCodesType, string name) =>
            (OpCode)opCodesType.GetField(name, BindingFlags.Public | BindingFlags.Static).GetValue(null);
        
        static ModkitCompiler()
        {
            var opCodesType = typeof(OpCode).Assembly.GetType("Mono.Cecil.Cil.OpCodes");
            Pop = GetOpCode(opCodesType, nameof(Pop));
            Ldstr = GetOpCode(opCodesType, nameof(Ldstr));
            Ldc_I4_0 = GetOpCode(opCodesType, nameof(Ldc_I4_0));
            Ldc_I4 = GetOpCode(opCodesType, nameof(Ldc_I4));
            Ldc_I4_S = GetOpCode(opCodesType, nameof(Ldc_I4_S));
            Newarr = GetOpCode(opCodesType, nameof(Newarr));
            Dup = GetOpCode(opCodesType, nameof(Dup));
            Stfld = GetOpCode(opCodesType, nameof(Stfld));
            Stelem_Ref = GetOpCode(opCodesType, nameof(Stelem_Ref));
            Call = GetOpCode(opCodesType, nameof(Call));
            Ldnull = GetOpCode(opCodesType, nameof(Ldnull));
            Ldarg_0 = GetOpCode(opCodesType, nameof(Ldarg_0));
            Ldarg_1 = GetOpCode(opCodesType, nameof(Ldarg_1));
            Ret = GetOpCode(opCodesType, nameof(Ret));

            ArrayTypeInternal = typeof(OpCode).Assembly.GetType("Mono.Cecil.ArrayType");
            AttributeUsageConstructor = typeof(AttributeUsageAttribute).GetConstructor(new[] { typeof(AttributeTargets) });

            var methodBodyRocksType =
                typeof(Mono.Cecil.Rocks.ILParser).Assembly.GetType("Mono.Cecil.Rocks.MethodBodyRocks");
            simplifyMacrosMethod = methodBodyRocksType.GetMethod("SimplifyMacros", BindingFlags.Static | BindingFlags.Public);
            optimizeMacrosMethod = methodBodyRocksType.GetMethod("OptimizeMacros", BindingFlags.Static | BindingFlags.Public);
        }
    }
}