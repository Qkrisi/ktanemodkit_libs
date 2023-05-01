using System;
using System.Reflection;

namespace Eltons.ReflectionKit
{
    public static class MemberInfoExtensions
    {
        public static bool HasAttribute<TAttr>(this MemberInfo member) where TAttr : Attribute =>
            member.GetCustomAttributes(typeof(TAttr), true).Length > 0;
    }
}