// Small pieces that newer C# features expect from the runtime. .NET Framework 4.8 doesn't have them,
// so they live here. The compiler finds them by name; nothing else uses them.

namespace System.Runtime.CompilerServices
{
    /// <summary>Lets records and <c>init</c> accessors compile for .NET Framework.</summary>
    internal static class IsExternalInit
    {
    }

    /// <summary>Lets <c>required</c> members compile for .NET Framework.</summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Field | AttributeTargets.Property, Inherited = false)]
    internal sealed class RequiredMemberAttribute : Attribute
    {
    }

    [AttributeUsage(AttributeTargets.All, AllowMultiple = true, Inherited = false)]
    internal sealed class CompilerFeatureRequiredAttribute : Attribute
    {
        public CompilerFeatureRequiredAttribute(string featureName) => FeatureName = featureName;

        public string FeatureName { get; }

        public bool IsOptional { get; init; }
    }
}

namespace System.Diagnostics.CodeAnalysis
{
    [AttributeUsage(AttributeTargets.Constructor, Inherited = false)]
    internal sealed class SetsRequiredMembersAttribute : Attribute
    {
    }
}
