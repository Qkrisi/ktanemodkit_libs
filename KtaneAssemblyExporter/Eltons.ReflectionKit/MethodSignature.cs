/* This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at http://mozilla.org/MPL/2.0/. */

using System;
using System.Linq;
using System.Reflection;
using System.Text;

namespace Eltons.ReflectionKit
{
    public class MethodSignature : MethodBaseSignature
    {
        public string Build(MethodInfo method, bool invokable, bool skipLast, string @namespace = null, bool skipAccessor = false)
        {
            var signatureBuilder = new StringBuilder();

            // Add our method accessors if it's not invokable
            if (!invokable)
            {
                if(!skipAccessor)
                    signatureBuilder.Append(BuildAccessor(method));
                signatureBuilder.Append(BuildModifiers(method));
                signatureBuilder.Append(TypeSignature.Build(method.ReturnType, @namespace: @namespace));
                signatureBuilder.Append(" ");
            }

            // Add method name
            signatureBuilder.Append(method.Name);

            // Add method generics
            if (method.IsGenericMethod)
            {
                signatureBuilder.Append(BuildGenerics(method, @namespace));
            }

            // Add method parameters
            signatureBuilder.Append(BuildArguments(method, invokable, skipLast, @namespace));

            return signatureBuilder.ToString();
        }
    }
}