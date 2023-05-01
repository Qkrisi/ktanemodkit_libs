/* This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at http://mozilla.org/MPL/2.0/. */

using System;
using System.Collections.Generic;
using System.Linq;

namespace Eltons.ReflectionKit
{
    public static class TypeSignature
    {
        public static readonly string[] AccessModifiers = new[]
        {
            "private",
            "private protected",
            "protected",
            "internal",
            "protected internal",
            "public"
        };
        
        public static int GetAccessModifier(Type type, Type referenceType)
        {
            if (type.Assembly.Equals(referenceType.Assembly))
                return 5;
            if (type.IsPublic || type.IsNestedPublic)
                return 5;
            if (type.IsNestedFamORAssem)
                return 4;
            if (type.IsNestedAssembly)
                return 3;
            if (type.IsNestedFamily)
                return 2;
            if (type.IsNestedFamANDAssem)
                return 1;
            return 0;
        }
        
        /// <summary>
        /// Get a fully qualified signature for <paramref name="type"/>
        /// </summary>
        /// <param name="type">Type. May be generic or <see cref="Nullable{T}"/></param>
        /// <param name="useFullName">Use type.FullName</param>
        /// <returns>Fully qualified signature</returns>
        public static string Build(Type _type, bool useFullName = true, bool skipNesting = false, IEnumerable<Type> GenericsOverride = null, string @namespace = null)
        {
            if (_type.IsNested && !skipNesting && !_type.IsGenericParameter && useFullName)
            {
                var nestingHierarchy = new List<Type>();
                var currentType = _type;
                while (currentType != null)
                {
                    nestingHierarchy.Insert(0, currentType);
                    currentType = currentType.DeclaringType;
                }
                int genericCount = 0;
                var genericSignature = "";
                var args = _type.GetGenericArguments();
                foreach (var pType in nestingHierarchy)
                {
                    var gtCount = pType.GetGenericArguments().Length-genericCount;
                    if (genericSignature != "")
                        genericSignature += ".";
                    genericSignature += Build(pType, pType == nestingHierarchy[0], true,
                        args.Skip(genericCount).Take(gtCount), @namespace);
                    genericCount += gtCount;
                }
                return genericSignature; //Build(_type.DeclaringType) + "." + Build(_type, false, true);
            }

            var type = _type.IsArray ? _type.GetElementType() : _type;
            Type underlyingNullableType;
            var isNullableType = type.IsNullable(out underlyingNullableType);

            var signatureType = isNullableType
                ? underlyingNullableType
                : type;

            var isGenericType = signatureType.IsGeneric();

            var signature = GetQualifiedTypeName(signatureType, useFullName && !_type.IsNested);

            if (isGenericType)
            {
                // Add the generic arguments
                signature += BuildGenerics(GenericsOverride ?? signatureType.GetGenericArguments(), @namespace);
            }

            if (isNullableType)
            {
                signature += "?";
            }

            if (_type.IsArray)
                signature += "[" + new string(',', _type.GetArrayRank() - 1) + "]";

            if (useFullName && @namespace != null && type.Namespace == @namespace && !type.IsGenericParameter)
                signature = signature.Substring(@namespace.Length + 1);

            return signature.Replace("+", ".");
        }

        /// <summary>
        /// Takes an <see cref="IEnumerable{T}"/> and creates a generic type signature (&lt;string, string&gt; for example)
        /// </summary>
        /// <param name="genericArgumentTypes"></param>
        /// <returns>Generic type signature like &lt;Type, ...&gt;</returns>
        public static string BuildGenerics(IEnumerable<Type> genericArgumentTypes, string @namespace)
        {
            var argumentSignatures = genericArgumentTypes.Select(a => Build(a, @namespace: @namespace));

            return "<" + string.Join(", ", argumentSignatures.ToArray()) + ">";
        }

        /// <summary>
        /// Gets the fully qualified type name of <paramref name="type"/>.
        /// This will use any keywords in place of types where possible (string instead of System.String for example)
        /// </summary>
        /// <param name="type"></param>
        /// <returns>The fully qualified name for <paramref name="type"/></returns>
        public static string GetQualifiedTypeName(Type type, bool useFullName)
        {
            switch (type.Name)
            {
                case "String":
                    return "string";
                
                case "Char":
                    return "char";
                
                case "Byte":
                    return "byte";
                case "SByte":
                    return "sbyte";

                case "Int16":
                    return "short";
                case "UInt16":
                    return "ushort";
                
                case "Int32":
                    return "int";
                case "UInt32":
                    return "uint";
                
                case "Int64":
                    return "long";
                case "UInt64":
                    return "ulong";
                
                case "Single":
                    return "float";
                
                case "Double":
                    return "double";

                case "Decimal":
                    return "decimal";

                case "Object":
                case "Object&":
                    return "object";

                case "Void":
                    return "void";

                case "Boolean":
                    return "bool";
            }

            //TODO: Figure out how type.FullName could be null and document (or remove) this conditional
            var signature = !useFullName
                ? type.Name
                : string.IsNullOrEmpty(type.FullName?.Trim() ?? "")
                    ? type.ToString()
                    : type.FullName;

            if (type.IsGeneric())
                signature = RemoveGenericTypeNameArgumentCount(signature);

            return signature;
        }

        /// <summary>
        /// This removes the `{argumentcount} from a the signature of a generic type
        /// </summary>
        /// <param name="genericTypeSignature">Signature of a generic type</param>
        /// <returns><paramref name="genericTypeSignature"/> without any argument count</returns>
        public static string RemoveGenericTypeNameArgumentCount(string genericTypeSignature)
        {
            var index = genericTypeSignature.IndexOf('`');
            return index > -1 ? genericTypeSignature.Substring(0, index) : genericTypeSignature;
        }
    }
}