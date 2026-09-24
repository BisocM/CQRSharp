using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace CQRSharp.Analyzers;

/// <summary>The symbol questions every analyzer asks, answered once.</summary>
internal static class SymbolExtensions
{
    /// <summary>Whether the type is, or implements, the given interface definition.</summary>
    public static bool Implements(this ITypeSymbol type, INamedTypeSymbol iface)
    {
        if (SymbolEqualityComparer.Default.Equals(type.OriginalDefinition, iface)) return true;
        foreach (var implemented in type.AllInterfaces)
            if (SymbolEqualityComparer.Default.Equals(implemented.OriginalDefinition, iface))
                return true;
        return false;
    }

    /// <summary>The symbol's first location in source, or <see langword="null" /> for a symbol from metadata.</summary>
    public static Location? FirstSourceLocation(this ISymbol symbol)
    {
        foreach (var location in symbol.Locations)
            if (location.IsInSource)
                return location;
        return null;
    }

    /// <summary>The location of the invoked member's name (the precise squiggle), or of the whole invocation.</summary>
    public static Location NameLocation(this IInvocationOperation invocation)
        => invocation.Syntax is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax memberAccess }
            ? memberAccess.Name.GetLocation()
            : invocation.Syntax.GetLocation();
}
