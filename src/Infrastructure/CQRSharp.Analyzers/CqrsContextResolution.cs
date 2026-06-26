using Microsoft.CodeAnalysis;

namespace CQRSharp.Analyzers;

/// <summary>
///     Shared helpers for resolving the context type a request declares. A request declares its context by deriving
///     from <c>RequestBase&lt;TContext&gt;</c> (directly or transitively, e.g. via <c>CommandBase&lt;TContext&gt;</c>);
///     this walks the base-type chain to find that <c>TContext</c>.
/// </summary>
internal static class CqrsContextResolution
{
    /// <summary>
    ///     Returns the context type argument a request declares via <c>RequestBase&lt;TContext&gt;</c>, walking up the
    ///     base-type chain. Returns <c>null</c> when the request does not derive from the generic request base (so the
    ///     caller can skip requests whose context cannot be determined statically).
    /// </summary>
    public static ITypeSymbol? GetDeclaredContext(ITypeSymbol requestType, INamedTypeSymbol requestBase)
    {
        for (var current = requestType as INamedTypeSymbol; current is not null; current = current.BaseType)
            if (current.IsGenericType && SymbolEqualityComparer.Default.Equals(current.OriginalDefinition, requestBase))
                return current.TypeArguments[0];

        return null;
    }
}
