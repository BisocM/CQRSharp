using Microsoft.CodeAnalysis;

namespace CQRSharp.Generators;

/// <summary>
///     Whether the code the generator emits into a compilation can name a symbol: accessible from that assembly (public,
///     or internal to it or to an assembly that grants it access with InternalsVisibleTo), never file-local, never marked
///     <c>[Obsolete]</c> as an error (CS0619, which no pragma suppresses), an array by its element type, a constructed
///     generic by every type argument.
/// </summary>
internal static class GeneratedCodeAccessibility
{
    public static bool IsAccessible(ITypeSymbol typeSymbol, Compilation compilation)
    {
        if (!IsNameable(typeSymbol)) return false;
        return compilation.IsSymbolAccessibleWithin(typeSymbol, compilation.Assembly);
    }

    public static bool IsAccessible(IMethodSymbol methodSymbol, Compilation compilation)
        => IsAccessible(methodSymbol.ContainingType, compilation) &&
           compilation.IsSymbolAccessibleWithin(methodSymbol, compilation.Assembly);

    // What the accessibility check does not cover: a file-local type is internal but cannot be named from another file,
    // a type marked [Obsolete] as an error cannot be named without an error, and a type parameter or an error type names
    // nothing generated code could write.
    private static bool IsNameable(ITypeSymbol typeSymbol)
    {
        switch (typeSymbol)
        {
            case IArrayTypeSymbol array:
                return IsNameable(array.ElementType);
            case INamedTypeSymbol { TypeKind: not TypeKind.Error } named:
                for (var current = named; current is not null; current = current.ContainingType)
                    if (current.IsFileLocal)
                        return false;
                if (GeneratedCodeSuppressions.IsObsoleteAsError(named))
                    return false;

                foreach (var typeArgument in named.TypeArguments)
                    if (typeArgument is not ITypeParameterSymbol && !IsNameable(typeArgument))
                        return false;

                return true;
            default:
                return false;
        }
    }
}
