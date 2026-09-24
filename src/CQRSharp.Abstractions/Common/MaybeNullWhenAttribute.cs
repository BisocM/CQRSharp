// ReSharper disable once CheckNamespace
#if !NETCOREAPP3_0_OR_GREATER
namespace System.Diagnostics.CodeAnalysis;

// Try-pattern contracts with a generic out value (IIdempotencyResultSerializer.TryDeserialize) carry this annotation;
// netstandard2.0 alone lacks it.
[AttributeUsage(AttributeTargets.Parameter)]
internal sealed class MaybeNullWhenAttribute : Attribute
{
    public MaybeNullWhenAttribute(bool returnValue) => ReturnValue = returnValue;

    public bool ReturnValue { get; }
}
#endif
