/* This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at http://mozilla.org/MPL/2.0/. */

using System;

namespace Eltons.ReflectionKit
{
    public static class TypeExtensionMethods
    {
        /// <summary>
        /// Is this type a generic type
        /// </summary>
        /// <param name="type"></param>
        /// <returns>True if generic, otherwise False</returns>
        public static bool IsGeneric(this Type type)
        {
            return type.IsGenericType
                   && type.Name.Contains("`");
        }

        public static bool IsNullable(this Type type, out Type underlyingType)
        {
            underlyingType = Nullable.GetUnderlyingType(type);
            return underlyingType != null;
        }
        
        public static bool HasAttribute<TAttr>(this Type type) where TAttr : Attribute =>
            type.GetCustomAttributes(typeof(TAttr), true).Length > 0;

        public static bool IsMonoBehaviour(this Type type)
        {
            if (!type.IsClass)
                return false;
            var cType = type.BaseType;
            while (cType != null && cType != typeof(object))
            {
                if (cType.FullName == "UnityEngine.MonoBehaviour")
                    return true;
                cType = cType.BaseType;
            }
            return false;
        }
    }
}