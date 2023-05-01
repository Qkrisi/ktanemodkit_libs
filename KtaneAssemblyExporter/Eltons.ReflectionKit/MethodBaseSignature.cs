/* This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at http://mozilla.org/MPL/2.0/. */

using System;
using System.Linq;
using System.Reflection;

namespace Eltons.ReflectionKit
{
    public abstract class MethodBaseSignature
    {
        private static string CalculateMinAccessModifier(MethodInfo method)
        {
            var modifierIndex = method.GetParameters()
                .Select(p => TypeSignature.GetAccessModifier(p.ParameterType, method.ReflectedType)).Concat(new int[] {int.MaxValue}).Min();
            return TypeSignature.AccessModifiers[
                Math.Min(modifierIndex, TypeSignature.GetAccessModifier(method.ReturnType, method.ReflectedType))] + " ";
        }
        
        public static string BuildAccessModifier(MethodBase _method)
        {
            if (!(_method is MethodInfo))
                return "public ";
            var methodInfo = (MethodInfo)_method;
            if (methodInfo.Equals(methodInfo.GetBaseDefinition()))
                return CalculateMinAccessModifier(methodInfo);
            var method = methodInfo.GetBaseDefinition();
            if (methodInfo.DeclaringType.Assembly.Equals(method.DeclaringType.Assembly))
                return CalculateMinAccessModifier(methodInfo);
            var signature = CalculateMinAccessModifier(methodInfo);
            if (method.IsAssembly)
            {
                signature = "internal ";

                if (method.IsFamily)
                    signature += "protected ";
            }
            else if (method.IsPublic)
            {
                signature = CalculateMinAccessModifier(methodInfo);
            }
            else if (method.IsPrivate)
            {
                signature = "private ";
            }
            else if (method.IsFamily)
            {
                signature = "protected ";
            }

            return signature;
        }
        
        public string BuildAccessor(MethodBase method)
        {
            if (method is ConstructorInfo && method.IsStatic)
                return "static ";
            if (method.DeclaringType.IsInterface)
                return "";

            string signature = BuildAccessModifier(method);   //null;

            if (method.IsStatic)
                signature += "static ";

            return signature;
        }

        public static string BuildModifiers(MethodBase method)
        {
            var modifiers = "";
            if (method is MethodInfo)
            {
                var methodInfo = (MethodInfo)method;
                if (methodInfo.DeclaringType.IsInterface)
                    return modifiers;
                var isOverride = !methodInfo.Equals(methodInfo.GetBaseDefinition());
                if (isOverride)
                    modifiers += "override ";
                else
                {
                    if (method.IsAbstract && !method.IsFinal)
                        modifiers += "abstract ";
                    else if (method.IsVirtual && !method.IsFinal)
                        modifiers += "virtual ";
                }
            }
            
            return modifiers;
        }

        public string BuildArguments(MethodBase method, bool invokable, bool skipLast, string @namespace)
        {
            var isExtensionMethod = method.IsDefined(typeof(System.Runtime.CompilerServices.ExtensionAttribute), false);
            var methodParameters = method.GetParameters().AsEnumerable();

            // If this signature is designed to be invoked and it's an extension method
            if (isExtensionMethod && invokable)
            {
                // Skip the first argument
                methodParameters = methodParameters.Skip(1);
            }

            if (skipLast)
            {
                var parameterInfos = methodParameters as ParameterInfo[] ?? methodParameters.ToArray();
                methodParameters = parameterInfos.Take(parameterInfos.Length - 1);
            }

            var methodParameterSignatures = methodParameters.Select(param =>
            {
                var signature = string.Empty;

                if (param.IsOut)
                    signature = "out ";
                else if (param.ParameterType.IsByRef)
                    signature = "ref ";
                else if (isExtensionMethod && param.Position == 0)
                    signature = "this ";

                if (!invokable)
                {
                    var type = param.ParameterType.IsByRef ? param.ParameterType.GetElementType() : param.ParameterType;
                    signature += TypeSignature.Build(type, @namespace: @namespace) + " ";
                }

                signature += param.Name;

                return signature;
            });

            var methodParameterString = "(" + string.Join(", ", methodParameterSignatures.ToArray()) + ")";

            return methodParameterString;
        }

        public string BuildGenerics(MethodBase method, string @namespace)
        {
            if (method == null) throw new ArgumentNullException(nameof(method));
            if (!method.IsGenericMethod) throw new ArgumentException($"{method.Name} is not generic.");

            return TypeSignature.BuildGenerics(method.GetGenericArguments(), @namespace);
        }
    }
}