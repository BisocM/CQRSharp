// ReSharper disable once CheckNamespace
#if !NETCOREAPP3_0_OR_GREATER
namespace System.Diagnostics.CodeAnalysis;

// Try-pattern contracts (INotificationSerializer.TryGetNotificationName) carry this annotation; netstandard2.0 alone lacks it.
[AttributeUsage(AttributeTargets.Parameter)]
internal sealed class NotNullWhenAttribute : Attribute
{
    public NotNullWhenAttribute(bool returnValue) => ReturnValue = returnValue;

    public bool ReturnValue { get; }
}
#endif
