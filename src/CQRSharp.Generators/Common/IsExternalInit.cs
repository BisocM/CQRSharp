// Polyfill so C# records and `init` accessors compile on the netstandard2.0 target (the BCL there lacks this type).
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit
    {
    }
}
