// ReSharper disable once CheckNamespace
#if !NET8_0_OR_GREATER
namespace System.Diagnostics.CodeAnalysis;

// The transport extension point (CQRSharp.Transports) is marked experimental, which the compiler reports at every use of
// it; netstandard2.0 alone lacks the attribute, and the compiler recognizes it by name.
[AttributeUsage(
    AttributeTargets.Assembly | AttributeTargets.Module | AttributeTargets.Class | AttributeTargets.Struct |
    AttributeTargets.Enum | AttributeTargets.Constructor | AttributeTargets.Method | AttributeTargets.Property |
    AttributeTargets.Field | AttributeTargets.Event | AttributeTargets.Interface | AttributeTargets.Delegate,
    Inherited = false)]
internal sealed class ExperimentalAttribute : Attribute
{
    public ExperimentalAttribute(string diagnosticId) => DiagnosticId = diagnosticId;

    public string DiagnosticId { get; }

    public string? UrlFormat { get; set; }
}
#endif
