using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using CQRSharp.Generators;
using CQRSharp.Shared;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CQRSharp.Generators.CqrsSourceGenerator;

public sealed partial class CqrsSourceGenerator
{
    // ============================================================================================================
    // Symbol helpers used only during extraction.
    // ============================================================================================================

    private static bool IsAccessibleFromGeneratedCode(ITypeSymbol typeSymbol)
    {
        // An array is exactly as accessible as its element type (a UserDto[] / byte[] result is perfectly bindable).
        if (typeSymbol is IArrayTypeSymbol arrayTypeSymbol)
            return IsAccessibleFromGeneratedCode(arrayTypeSymbol.ElementType);

        if (typeSymbol is not INamedTypeSymbol namedTypeSymbol) return false;

        for (var current = namedTypeSymbol; current is not null; current = current.ContainingType)
        {
            // A file-local type reads as internal but cannot be named from the generated file.
            if (current.IsFileLocal) return false;
            if (current.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Internal))
                return false;
        }

        // A constructed generic is only nameable when every type argument is too (List<PrivateDto>).
        foreach (var typeArgument in namedTypeSymbol.TypeArguments)
            if (typeArgument is not ITypeParameterSymbol && !IsAccessibleFromGeneratedCode(typeArgument))
                return false;

        return true;
    }

    // Declared members only would silently drop a base class's state (an abstract DomainEvent's EventId/OccurredAt)
    // from the payload. Walk the base chain, most-derived first, keeping the first property seen per name so an
    // override or a "new" member shadows its base.
    private static IEnumerable<IPropertySymbol> GetDeclaredAndInheritedProperties(INamedTypeSymbol type)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var current = type; current is not null && current.SpecialType != SpecialType.System_Object; current = current.BaseType)
            foreach (var property in current.GetMembers().OfType<IPropertySymbol>())
                if (seen.Add(property.Name))
                    yield return property;
    }

    private static bool IsAccessibleFromGeneratedCode(IMethodSymbol methodSymbol)
    {
        if (methodSymbol.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Internal))
            return false;
        return IsAccessibleFromGeneratedCode(methodSymbol.ContainingType);
    }

    private static IEnumerable<INamedTypeSymbol> GetInterfacesAndBaseInterfaces(ITypeSymbol typeSymbol)
    {
        var allInterfaces = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        var typesToProcess = new Queue<ITypeSymbol>();
        typesToProcess.Enqueue(typeSymbol);

        while (typesToProcess.Count > 0)
        {
            var currentType = typesToProcess.Dequeue();
            if (currentType is null) continue;

            foreach (var iface in currentType.Interfaces.Where(iface => allInterfaces.Add(iface)))
                typesToProcess.Enqueue(iface);

            if (currentType.BaseType != null)
                typesToProcess.Enqueue(currentType.BaseType);
        }

        return allInterfaces;
    }

    private static IEnumerable<INamedTypeSymbol> GetAllKnownHandlerSymbols(Compilation compilation)
    {
        var known = CqrsKnownSymbols.For(compilation);
        var commandHandlerSymbols = new[] { known.ICommandHandler1, known.ICommandHandler2 };
        var resultCommandHandlerSymbols = new[] { known.IResultCommandHandler2, known.IResultCommandHandler3 };
        var queryHandlerSymbols = new[] { known.IQueryHandler2, known.IQueryHandler3 };
        var streamHandlerSymbols = new[] { known.IStreamRequestHandler2, known.IStreamRequestHandler3 };

        return commandHandlerSymbols
            .Concat(resultCommandHandlerSymbols)
            .Concat(queryHandlerSymbols)
            .Concat(streamHandlerSymbols)
            .Concat(new[]
            {
                known.INotificationHandler,
                known.IRequestValidator1,
                known.IRequestExceptionHandler3,
                known.IRequestExceptionAction2,
                known.IPipelineBehavior,
                known.IStreamPipelineBehavior
            })
            .Where(s => s is not null)
            .Cast<INamedTypeSymbol>();
    }

    private static ITypeSymbol? GetRequestContextType(ITypeSymbol requestTypeSymbol, Compilation compilation)
    {
        var requestBaseSymbol = CqrsKnownSymbols.For(compilation).RequestBaseGeneric;
        if (requestBaseSymbol is null) return null;

        var current = requestTypeSymbol;
        while (current != null)
        {
            if (current is INamedTypeSymbol { IsGenericType: true } namedType &&
                SymbolEqualityComparer.Default.Equals(namedType.OriginalDefinition, requestBaseSymbol))
                return namedType.TypeArguments[0];

            current = current.BaseType;
        }

        return null;
    }

    private static bool InheritsOrImplements(ITypeSymbol type, ITypeSymbol baseType)
    {
        if (SymbolEqualityComparer.Default.Equals(type.OriginalDefinition, baseType.OriginalDefinition))
            return true;

        return GetInterfacesAndBaseInterfaces(type).Any(i => SymbolEqualityComparer.Default.Equals(i.OriginalDefinition, baseType.OriginalDefinition)) ||
               (type.BaseType != null && InheritsOrImplements(type.BaseType, baseType));
    }

    private static string EscapeIdentifier(string identifier)
        => SyntaxFacts.GetKeywordKind(identifier) != SyntaxKind.None ? "@" + identifier : identifier;

    private static string RenderTypedConstant(TypedConstant constant)
    {
        if (constant.IsNull) return "null";

        // TypedConstant.Value throws for an array constant (its elements are in Values), so read it only for scalars.
        var value = constant.Kind == TypedConstantKind.Array ? null : constant.Value;

        switch (constant.Kind)
        {
            case TypedConstantKind.Primitive:
                // FormatPrimitive yields a culture-invariant, fully escaped C# literal (quoted/escaped strings and
                // chars, true/false). Other numerics carry no literal suffix, so they are cast to their exact type —
                // the parenthesised operand keeps a negative value from parsing as a subtraction.
                var literal = SymbolDisplay.FormatPrimitive(value!, quoteStrings: true, useHexadecimalNumbers: false);
                return value is string or char or bool or int
                    ? literal
                    : $"({constant.Type!.ToDisplayString(Fq)})({literal})";
            case TypedConstantKind.Enum:
                if (constant.Type is not INamedTypeSymbol enumType)
                    return $"({constant.Type!.ToDisplayString(Fq)}){value}";
                var member = enumType.GetMembers().OfType<IFieldSymbol>()
                    .FirstOrDefault(f => f.ConstantValue is not null && f.ConstantValue.Equals(value));
                return member is not null
                    ? $"{enumType.ToDisplayString(Fq)}.{member.Name}"
                    : $"({enumType.ToDisplayString(Fq)}){value}";
            case TypedConstantKind.Type:
                return $"typeof({((ITypeSymbol)value!).ToDisplayString(Fq)})";
            case TypedConstantKind.Array:
                // Includes a params argument: [RequireRoles("a", "b")] arrives as one string[] constant.
                var elementType = ((IArrayTypeSymbol)constant.Type!).ElementType.ToDisplayString(Fq);
                return $"new {elementType}[] {{ {string.Join(", ", constant.Values.Select(RenderTypedConstant))} }}";
            case TypedConstantKind.Error:
            default:
                return "null";
        }
    }

    private static string GetJsonPropertyName(IPropertySymbol property, INamedTypeSymbol? jsonPropertyNameAttributeSymbol)
    {
        if (jsonPropertyNameAttributeSymbol is not null)
        {
            var attr = property.GetAttributes().FirstOrDefault(a =>
                a.AttributeClass is not null &&
                SymbolEqualityComparer.Default.Equals(a.AttributeClass, jsonPropertyNameAttributeSymbol));
            if (attr is not null)
            {
                var arg = attr.ConstructorArguments.FirstOrDefault();
                if (arg.Value is string name && !string.IsNullOrWhiteSpace(name))
                    return name;
            }
        }

        return ToCamelCase(property.Name);
    }

    private static string ToCamelCase(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        if (!char.IsUpper(name[0])) return name;

        var chars = name.ToCharArray();

        for (var i = 0; i < chars.Length; i++)
        {
            if (i == 1 && !char.IsUpper(chars[i]))
                break;

            var hasNext = i + 1 < chars.Length;
            if (i > 0 && hasNext && !char.IsUpper(chars[i + 1]))
                break;

            chars[i] = char.ToLowerInvariant(chars[i]);
        }

        return new string(chars);
    }

    private static string CreateIdentifierSuffix(string text, string prefix)
    {
        var hash = StableNameHash(text);

        var sb = new StringBuilder();
        sb.Append(prefix);

        var maxLen = Math.Min(text.Length, 40);
        for (var i = 0; i < maxLen; i++)
        {
            var c = text[i];
            if ((c >= 'a' && c <= 'z') ||
                (c >= 'A' && c <= 'Z') ||
                (c >= '0' && c <= '9') ||
                c == '_')
                sb.Append(c);
            else
                sb.Append('_');
        }

        sb.Append('_');
        sb.Append(hash.ToString("X8"));
        return sb.ToString();
    }

    private static uint StableNameHash(string stableName)
    {
        // FNV-1a 32-bit
        unchecked
        {
            var hash = 2166136261u;
            for (var i = 0; i < stableName.Length; i++)
            {
                hash ^= stableName[i];
                hash *= 16777619u;
            }

            return hash;
        }
    }
}
