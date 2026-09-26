// ReSharper disable once CheckNamespace
#if !NET5_0_OR_GREATER
namespace System.Runtime.CompilerServices;

/// <summary>
///     The marker init-only setters and records compile against. netstandard2.0 has no such type; every other target
///     framework ships it in-box, so the polyfill is compiled for netstandard2.0 alone.
/// </summary>
internal static class IsExternalInit
{
}
#endif