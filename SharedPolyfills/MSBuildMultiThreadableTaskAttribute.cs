using System;

namespace Microsoft.Build.Framework
{
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    public sealed class MSBuildMultiThreadableTaskAttribute : Attribute { }
}
