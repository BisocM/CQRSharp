using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace CQRSharp.Generators;

/// <summary>
///     The diagnostics that naming a deprecated or experimental symbol raises, which generated code suppresses so a
///     warnaserror build of the consumer keeps compiling: the diagnostic id an <c>[Obsolete]</c> declares
///     (<c>DiagnosticId</c>) and the id of an <c>[Experimental]</c> (an error by default). The compiler's own obsolete
///     warnings (CS0612, CS0618) are suppressed in every generated file regardless. An <c>[Obsolete]</c> declared as an
///     error (CS0619) cannot be suppressed: such a type is not nameable by generated code at all (see
///     <see cref="GeneratedCodeAccessibility" />).
/// </summary>
internal static class GeneratedCodeSuppressions
{
    private const string ObsoleteAttributeName = "System.ObsoleteAttribute";
    private const string ExperimentalAttributeName = "System.Diagnostics.CodeAnalysis.ExperimentalAttribute";

    /// <summary>The suppressions every generated file that names consumer types starts with.</summary>
    public static readonly string[] CompilerObsoleteIds = ["CS0612", "CS0618"];

    /// <summary>
    ///     Adds the ids naming <paramref name="type" /> raises: its own, its containing types', its module's and
    ///     assembly's, and those of every type argument and array element type it is built from.
    /// </summary>
    public static void CollectType(ITypeSymbol? type, ISet<string> ids)
    {
        switch (type)
        {
            case IArrayTypeSymbol array:
                CollectType(array.ElementType, ids);
                return;
            case INamedTypeSymbol named:
                for (var current = named; current is not null; current = current.ContainingType)
                    CollectAttributes(current.GetAttributes(), ids);
                if (named.ContainingModule is { } module) CollectAttributes(module.GetAttributes(), ids);
                if (named.ContainingAssembly is { } assembly) CollectAttributes(assembly.GetAttributes(), ids);
                foreach (var argument in named.TypeArguments)
                    CollectType(argument, ids);
                return;
        }
    }

    /// <summary>
    ///     Adds the ids of <paramref name="type" /> and of the public properties and fields reachable from it, which
    ///     the generated outbox serializer and request fingerprinter read and write member by member.
    /// </summary>
    public static void CollectMembers(ITypeSymbol type, ISet<string> ids)
    {
        var visited = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
        Walk(type);

        void Walk(ITypeSymbol current)
        {
            if (current is IArrayTypeSymbol array)
            {
                Walk(array.ElementType);
                return;
            }

            if (current is not INamedTypeSymbol named || !visited.Add(named)) return;
            CollectType(named, ids);
            foreach (var argument in named.TypeArguments)
                Walk(argument);

            // The framework's own types carry no consumer deprecation worth walking into.
            if (named.ContainingNamespace?.ToDisplayString() is { } ns && (ns == "System" || ns.StartsWith("System.", StringComparison.Ordinal)))
                return;

            for (var owner = named; owner is not null; owner = owner.BaseType)
                foreach (var member in owner.GetMembers())
                {
                    if (member.IsStatic || member.DeclaredAccessibility != Accessibility.Public) continue;
                    var memberType = member switch
                    {
                        IPropertySymbol property => property.Type,
                        IFieldSymbol field => field.Type,
                        _ => null
                    };
                    if (memberType is null) continue;

                    CollectAttributes(member.GetAttributes(), ids);
                    Walk(memberType);
                }
        }
    }

    /// <summary>Whether <paramref name="type" /> or a type containing it is marked <c>[Obsolete]</c> as an error.</summary>
    public static bool IsObsoleteAsError(INamedTypeSymbol type)
    {
        for (var current = type; current is not null; current = current.ContainingType)
            foreach (var attribute in current.GetAttributes())
                if (IsObsolete(attribute) && attribute.ConstructorArguments.Length == 2 && attribute.ConstructorArguments[1].Value is true)
                    return true;
        return false;
    }

    private static void CollectAttributes(IEnumerable<AttributeData> attributes, ISet<string> ids)
    {
        foreach (var attribute in attributes)
        {
            if (IsObsolete(attribute))
            {
                foreach (var named in attribute.NamedArguments)
                    if (named.Key == "DiagnosticId" && named.Value.Value is string id && IsDiagnosticId(id))
                        ids.Add(id);
            }
            else if (attribute.AttributeClass?.ToDisplayString() == ExperimentalAttributeName &&
                     attribute.ConstructorArguments.FirstOrDefault().Value is string id && IsDiagnosticId(id))
            {
                ids.Add(id);
            }
        }
    }

    private static bool IsObsolete(AttributeData attribute) => attribute.AttributeClass?.ToDisplayString() == ObsoleteAttributeName;

    // What a #pragma warning line can carry: a diagnostic id is a letter followed by letters, digits and underscores.
    private static bool IsDiagnosticId(string id)
        => id.Length > 0 && char.IsLetter(id[0]) && id.All(c => char.IsLetterOrDigit(c) || c == '_');
}
