using Microsoft.CodeAnalysis;
using System.Linq;
using CQRSharp.Abstractions.SourceGeneration;

namespace CQRSharp.Generators.Types;

public sealed partial class CqrsSourceGenerator
{
    private static ITypeSymbol InferResultTypeSymbol(Compilation compilation, INamedTypeSymbol requestSymbol)
    {
        var iQuerySymbol = compilation.GetTypeByMetadataName(TypeStrings.IQuery);
        var iQuery = requestSymbol.AllInterfaces.FirstOrDefault(i => i.OriginalDefinition.Equals(iQuerySymbol, SymbolEqualityComparer.Default));
        return iQuery?.TypeArguments[0] ?? compilation.GetTypeByMetadataName(TypeStrings.CommandResult)!;
    }

    private static ITypeSymbol? GetRequestContextType(ITypeSymbol requestTypeSymbol, Compilation compilation)
    {
        var requestBaseSymbol = compilation.GetTypeByMetadataName(TypeStrings.RequestBaseGeneric);
        if (requestBaseSymbol is null)
        {
            return null;
        }

        var current = requestTypeSymbol.BaseType;
        while (current != null)
        {
            if (current.IsGenericType && SymbolEqualityComparer.Default.Equals(current.OriginalDefinition, requestBaseSymbol))
            {
                return current.TypeArguments[0];
            }

            current = current.BaseType;
        }

        return null;
    }

    private static string GenerateAttributeArrayCode(ITypeSymbol requestTypeSymbol, INamedTypeSymbol attributeInterfaceSymbol, string fullyQualifiedInterfaceName)
    {
        var attributeInstances = requestTypeSymbol.GetAttributes()
            .Where(attr => attr.AttributeClass != null && InheritsOrImplements(attr.AttributeClass, attributeInterfaceSymbol))
            .ToList();

        if (!attributeInstances.Any()) return $"System.Array.Empty<{fullyQualifiedInterfaceName}>()";

        var instancesCode = attributeInstances.Select(attr =>
        {
            var attrClassName = attr.AttributeClass!.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            var args = string.Join(", ", attr.ConstructorArguments.Select(GenerateTypedConstant));
            return $"new {attrClassName}({args})";
        });

        return $"new {fullyQualifiedInterfaceName}[] {{ {string.Join(", ", instancesCode)} }}";
    }

    private static string GenerateSensitivePropertiesCode(ITypeSymbol requestTypeSymbol)
    {
        const string sensitiveDataAttributeName = "SensitiveDataAttribute";
        var propertySensitivityFullyQualifiedName = $"global::{TypeStrings.PropertySensitivity}";

        var sensitiveProps = requestTypeSymbol.GetMembers()
            .OfType<IPropertySymbol>()
            .Where(prop => prop.GetAttributes().Any(attr => attr.AttributeClass?.Name == sensitiveDataAttributeName))
            .Select(prop => $"new {propertySensitivityFullyQualifiedName}(\"{prop.Name}\", true)")
            .ToList();

        return !sensitiveProps.Any()
            ? $"System.Array.Empty<{propertySensitivityFullyQualifiedName}>()"
            : $"new {propertySensitivityFullyQualifiedName}[] {{ {string.Join(", ", sensitiveProps)} }}";
    }

    private static string GenerateTypedConstant(TypedConstant constant)
    {
        if (constant.IsNull) return "null";
        var value = constant.Value;

        switch (constant.Kind)
        {
            case TypedConstantKind.Primitive:
                return value switch
                {
                    string s => $"\"{s.Replace("\"", "\\\"")}\"",
                    bool b => b.ToString().ToLowerInvariant(),
                    _ => value?.ToString() ?? "null",
                };
            case TypedConstantKind.Enum:
                if (constant.Type is not INamedTypeSymbol enumType) return $"({constant.Type!.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}){value}";
                var member = enumType.GetMembers().OfType<IFieldSymbol>().FirstOrDefault(f => f.ConstantValue is not null && f.ConstantValue.Equals(value));
                return member is not null
                    ? $"{enumType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}.{member.Name}"
                    : $"({enumType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}){value}";
            case TypedConstantKind.Type:
                return $"typeof({((ITypeSymbol)value!).ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)})";
            default:
                return "null";
        }
    }

    private static bool InheritsOrImplements(ITypeSymbol type, ITypeSymbol baseType)
    {
        if (SymbolEqualityComparer.Default.Equals(type, baseType)) return true;
        if (baseType.TypeKind == TypeKind.Interface &&
            type.AllInterfaces.Any(iface => SymbolEqualityComparer.Default.Equals(iface.OriginalDefinition, baseType.OriginalDefinition)))
        {
            return true;
        }

        var current = type.BaseType;
        while (current != null)
        {
            if (SymbolEqualityComparer.Default.Equals(current.OriginalDefinition, baseType.OriginalDefinition)) return true;
            current = current.BaseType;
        }

        return false;
    }
}