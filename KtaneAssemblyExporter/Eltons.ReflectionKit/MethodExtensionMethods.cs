/* This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at http://mozilla.org/MPL/2.0/. */

using System;
using System.Reflection;

namespace Eltons.ReflectionKit
{
    public static class MethodExtensionMethods
    {
        public static string GetSignature(this MethodInfo method, bool isInvokable, bool skipLast = false, string @namespace = null, bool skipAccessor = false)
        {
            if (method.Name == "OnPointerClick")
                Console.WriteLine($"{method}: {skipAccessor}");
            return new MethodSignature().Build(method, isInvokable, skipLast, @namespace, skipAccessor);
        }
    }
}