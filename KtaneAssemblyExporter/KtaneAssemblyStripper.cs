using System;
using System.Text;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Eltons.ReflectionKit;

public static class KtaneAssemblyStripper
{
    //Ignore types that only exist in the library of one platform for compatibility reasons
    private static readonly string[] IgnoredTypes = new[]
    {
        "Assets.Scripts.Platform.PC.PCPlatformUtil",
        "Assets.Scripts.Platform.OSX.OSXPlatformUtil",
        "Assets.Scripts.Platform.Linux.LinuxPlatformUtil",
        "Assets.Scripts.Platform.IOS.IOSPlatformUtil",
        "Assets.Scripts.Platform.PS4.PS4PlatformUtil"
    };

    public static readonly BindingFlags Flags = BindingFlags.Public
                                            | BindingFlags.NonPublic
                                            | BindingFlags.Instance
                                            | BindingFlags.Static
                                            | BindingFlags.GetField
                                            | BindingFlags.SetField
                                            | BindingFlags.GetProperty
                                            | BindingFlags.SetProperty
                                            | BindingFlags.DeclaredOnly;

    public static string BasePath;
    public static string StripPath;

    private static void EnsureAbsoluteDirectoryExists(string dir)
    {
        if (!String.IsNullOrEmpty(dir) && dir != BasePath && !Directory.Exists(dir))
        {
            EnsureAbsoluteDirectoryExists(Path.GetDirectoryName(dir));
            Directory.CreateDirectory(dir);
        }
    }

    public static void StripType(Type type, TextWriter stream, bool addHideInInspector, KtaneAssemblyExporter.DomainProxy proxy)
    {
        if (IgnoredTypes.Contains(type.FullName) || (type.IsNested && type.HasAttribute<CompilerGeneratedAttribute>() &&
            !type.DeclaringType.HasAttribute<CompilerGeneratedAttribute>() && type.BaseType != typeof(MulticastDelegate)))
            return;
        if (type.BaseType == typeof(MulticastDelegate))
        {
            var invokeMethod = type.GetMethod("Invoke", Flags);
            var methodSignature = invokeMethod.GetSignature(false);
            stream.WriteLine(methodSignature.Replace("virtual", "delegate")
                .Replace("Invoke", TypeSignature.Build(type, false)) + ";");
            return;
        }

        var t = type.IsEnum ? "enum" : type.IsInterface ? "interface" : type.IsValueType ? "struct" : "class";
        var inheritance = "";
        if (!type.IsValueType && !type.IsEnum)
        {
            var baseType = type.BaseType;
            if (baseType != null && baseType != typeof(object))
                inheritance += TypeSignature.Build(baseType);

            foreach (var interfaceType in type.GetInterfaces()
                         .Except(type.BaseType?.GetInterfaces() ?? Type.EmptyTypes))
            {
                if (inheritance != "")
                    inheritance += ", ";
                inheritance += TypeSignature.Build(interfaceType);
            }
        }

        if (inheritance != "")
            inheritance = $" : {inheritance}";
        var modifiers = "";
        if (type.IsAbstract && type.IsSealed)
            modifiers += "static ";
        else if (!type.IsInterface && type.IsAbstract)
            modifiers += "abstract ";
        else if (!type.IsValueType && type.IsSealed)
            modifiers += "sealed ";
        var isMonoBehaviour = type.IsMonoBehaviour();
        if(isMonoBehaviour && !type.IsAbstract && !type.IsSealed && (type.IsPublic || type.IsNestedPublic))
            proxy.AddComponentType(type.FullName.Replace('+', '.'));
        var serializable = type.HasAttribute<SerializableAttribute>();
        if(serializable)
            stream.WriteLine("[System.Serializable]");
        stream.Write($"public {modifiers}{t} {TypeSignature.Build(type, false, true)}{inheritance}");
        if (type.IsEnum)
        {
            var numType = Enum.GetUnderlyingType(type);
            stream.WriteLine($": {TypeSignature.GetQualifiedTypeName(numType, false)}");
            stream.WriteLine("{");
            foreach (var name in Enum.GetNames(type))
                stream.WriteLine($"{name} = {Convert.ChangeType(Enum.Parse(type, name, false), numType)},");
            goto closeType;
        }

        stream.WriteLine("{");
        foreach (var nestedType in type.GetNestedTypes(Flags))
            StripType(nestedType, stream, addHideInInspector, proxy);
        var fields = type.GetFields(Flags);
        foreach (var field in fields)
        {
            if (field.HasAttribute<CompilerGeneratedAttribute>())
                continue;
            var isHidden = false;
            var hasSerializeField = field.GetCustomAttributes(true)
                .Any(attr => attr.GetType().FullName == "UnityEngine.SerializeField");
            if (addHideInInspector && ((!field.IsPublic && (serializable || isMonoBehaviour) && !hasSerializeField) || field
                    .GetCustomAttributes(true)
                    .Any(attr => attr.GetType().FullName == "UnityEngine.HideInInspector")))
            {
                if(!hasSerializeField)
                    isHidden = true;
                stream.WriteLine("[UnityEngine.HideInInspector]");
            }
            if(!isHidden && hasSerializeField)
                stream.WriteLine("[UnityEngine.SerializeField]");
            if (field.HasAttribute<NonSerializedAttribute>())
                stream.WriteLine("[System.NonSerialized]");
            stream.WriteLine(
                $"{TypeSignature.AccessModifiers[TypeSignature.GetAccessModifier(field.FieldType, type)]} {(field.IsStatic ? "static " : "")}{TypeSignature.Build(field.FieldType, @namespace: type.Namespace)} {field.Name};");
        }

        var interfaceMethods = !type.IsInterface
            ? type.GetInterfaces()
                .SelectMany(interfaceType => type.GetInterfaceMap(interfaceType).TargetMethods).ToList()
            : new List<MethodInfo>();
        var accessorMethods = new List<MethodInfo>();
        foreach (var property in type.GetProperties(Flags))
        {
            if (property.HasAttribute<CompilerGeneratedAttribute>())
                continue;
            var getter = property.GetGetMethod(true);
            var setter = property.GetSetMethod(true);
            var propertyTypeSignature = TypeSignature.Build(property.PropertyType, @namespace: type.Namespace);
            bool isAbstract;
            bool isVirtual;
            string accessModifier;
            var skipAccessor = false;
            if (getter != null)
            {
                isAbstract = getter.IsAbstract && !getter.IsFinal;
                isVirtual = getter.IsVirtual && !getter.IsFinal;
                accessModifier = MethodBaseSignature.BuildAccessModifier(getter);
                skipAccessor = !(!getter.DeclaringType.IsInterface &&
                                 (!getter.Equals(getter.GetBaseDefinition()) || !getter.IsFinal)) &&
                               interfaceMethods.Contains(getter);
            }
            else
            {
                isAbstract = setter.IsAbstract && !setter.IsFinal;
                isVirtual = setter.IsVirtual && !setter.IsFinal;
                accessModifier = MethodBaseSignature.BuildAccessModifier(setter);
                skipAccessor = !(!setter.DeclaringType.IsInterface &&
                                 (!setter.Equals(setter.GetBaseDefinition()) || !setter.IsFinal)) &&
                               interfaceMethods.Contains(setter);
            }

            var isOverride = !(getter?.Equals(getter.GetBaseDefinition()) ??
                               setter.Equals(setter.GetBaseDefinition()));
            var isIndexer = property.Name == "Item" && (getter == null
                ? setter.GetParameters().Length > 1
                : getter.GetParameters().Length > 0);
            var signature = property.Name;
            if (isIndexer)
            {
                if (getter != null)
                {
                    var getterSignature = getter.GetSignature(false,
                        skipAccessor: skipAccessor);
                    var index = getterSignature.IndexOf("get_Item");
                    signature = getterSignature.Substring(index).Replace("get_Item(", "this[").Replace(')', ']');
                }
                else
                {
                    var setterSignature = setter.GetSignature(false, true,
                        skipAccessor: skipAccessor);
                    var index = setterSignature.IndexOf("set_Item");
                    signature = setterSignature.Substring(index).Replace("set_Item(", "this[").Replace(')', ']');
                }
            }

            stream.WriteLine(
                $"{(!type.IsInterface && !property.Name.Contains(".") ? accessModifier : "")}{(type.IsInterface ? "" : isAbstract ? "abstract " : isOverride ? "override " : isVirtual ? "virtual " : "")}{(getter?.IsStatic ?? setter.IsStatic ? "static " : "")}{propertyTypeSignature} {signature}");
            stream.WriteLine("{");
            if (getter != null)
            {
                accessorMethods.Add(getter);
                stream.WriteLine(isAbstract ? "get;" : $"get {{return default({propertyTypeSignature});}}");
            }

            if (setter != null)
            {
                accessorMethods.Add(setter);
                stream.WriteLine(isAbstract ? "set;" : "set {}");
            }

            stream.WriteLine("}");
        }

        foreach (var method in type.GetMethods(Flags).Where(m => !accessorMethods.Contains(m)))
        {
            if (method.HasAttribute<CompilerGeneratedAttribute>())
                continue;
            if (method.Name == "Finalize" && method.GetBaseDefinition().DeclaringType == typeof(object))
            {
                stream.WriteLine($"~{TypeSignature.RemoveGenericTypeNameArgumentCount(type.Name)}() {{}}");
                continue;
            }

            var signature = method.GetSignature(false, @namespace: type.Namespace,
                skipAccessor: method.Name.Contains('.') /*,
                    skipAccessor: !(!method.DeclaringType.IsInterface &&
                                    (!method.Equals(method.GetBaseDefinition()) || !method.IsFinal)) &&
                                  interfaceMethods.Contains(method)*/);
            if (method.Name == "op_Implicit" || method.Name == "op_Explicit" && method.IsStatic)
            {
                signature = signature.Replace(" " + method.Name, "").Replace(" static ",
                    " static " + method.Name.Replace("op_", "").ToLower() + " operator ");
            }

            if (signature ==
                "UnityEngine.Transform UnityEngine.UI.ICanvasElement.get_transform()") //I don't know why this specific property shows up as a normal method
            {
                stream.WriteLine("UnityEngine.Transform UnityEngine.UI.ICanvasElement.transform {");
                stream.WriteLine("get {return default(UnityEngine.Transform);}");
                stream.WriteLine("}");
                continue;
            }

            stream.Write(signature);
            if (type.IsInterface || (method.IsAbstract && !method.IsFinal))
            {
                stream.WriteLine(";");
                continue;
            }

            stream.WriteLine("{");
            foreach (var parameter in method.GetParameters().Where(p => p.IsOut))
                stream.WriteLine(
                    $"{parameter.Name} = default({TypeSignature.Build(parameter.ParameterType.IsByRef ? parameter.ParameterType.GetElementType() : parameter.ParameterType, @namespace: type.Namespace)});");

            if (method.ReturnType != typeof(void))
                stream.WriteLine(
                    $"return default({TypeSignature.Build(method.ReturnType, @namespace: type.Namespace)});");
            stream.WriteLine("}");
        }

        var baseConstructor =
            type.BaseType?.GetConstructors(Flags).OrderBy(c => c.GetParameters().Length)
                .FirstOrDefault(c => !c.IsStatic);
        foreach (var constructor in type.GetConstructors(Flags))
        {
            if (constructor.HasAttribute<CompilerGeneratedAttribute>())
                continue;
            stream.Write(constructor.GetSignature(false, @namespace: type.Namespace));
            if (type.IsValueType && !constructor.IsStatic)
                stream.WriteLine(" : this()");
            else if (type.IsClass && !constructor.IsStatic && baseConstructor != null)
                stream.WriteLine(
                    $" : base({string.Join(", ", baseConstructor.GetParameters().Select(p => $"default({TypeSignature.Build(p.ParameterType, @namespace: type.Namespace)})").ToArray())})");
            else stream.WriteLine("");
            stream.WriteLine("{");
            stream.WriteLine("}");
        }

        closeType:
        stream.WriteLine("}");
    }

    public static void StartTypeStrip(Type type, TextWriter stream, bool addHideInInspector, KtaneAssemblyExporter.DomainProxy proxy)
    {
        stream.WriteLine("#pragma warning disable 108, 114, 618");
        var writeNamespace = type.Namespace != null;
        if (writeNamespace)
        {
            stream.WriteLine($"namespace {type.Namespace}");
            stream.WriteLine("{");
        }

        StripType(type, stream, addHideInInspector, proxy);
        if (writeNamespace)
            stream.WriteLine("}");
        stream.Flush();
    }

    public static void StripAssembly(Assembly asm, bool addHideInInspector, KtaneAssemblyExporter.DomainProxy proxy)
    {
        var asmName = asm.GetName().Name;
        var dp = Path.Combine(StripPath, asmName);
        var files = asm.GetTypes().Where(t => !t.IsNested).ToArray();
        var delta = 1f / files.Length;
        proxy.Progress = 0;
        EnsureAbsoluteDirectoryExists(dp + "/Properties");
        File.WriteAllLines(dp + "/Properties/AssemblyInfo.cs", new[]
        {
            "using System.Reflection;",
            "using System.Runtime.CompilerServices;",
            "using System.Runtime.InteropServices;",
            $"[assembly: AssemblyTitle(\"{asmName}\")]",
            "[assembly: AssemblyDescription(\"\")]",
            "[assembly: AssemblyConfiguration(\"\")]",
            "[assembly: AssemblyCompany(\"\")]",
            $"[assembly: AssemblyProduct(\"{asmName}\")]",
            "[assembly: AssemblyCopyright(\"Copyright ©  2023\")]",
            "[assembly: AssemblyTrademark(\"\")]",
            "[assembly: AssemblyCulture(\"\")]",
            "[assembly: ComVisible(false)]",
            "[assembly: AssemblyVersion(\"0.0.0.0\")]",
            "[assembly: AssemblyFileVersion(\"0.0.0.0\")]"
        }, Encoding.UTF8);
        foreach (var type in files)
        {
            if (type.Name == "<PrivateImplementationDetails>")
                continue;
            proxy.CurrentAction = $"Exporting {type.FullName}";
            var directory = dp;
            if (type.Namespace != null)
                directory += "/" + type.Namespace.Replace('.', '/');
            EnsureAbsoluteDirectoryExists(directory);
            using (var file = new FileStream(Path.Combine(directory, type.Name.Replace('`', '_') + ".cs"),
                       FileMode.Create,
                       FileAccess.Write))
            {
                using (var stream = new StreamWriter(file))
                    StartTypeStrip(type, stream, addHideInInspector, proxy);
            }

            proxy.Progress += delta;
        }
    }
}