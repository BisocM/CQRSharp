using System.Collections.Generic;
using System.Linq;
using CQRSharp.Abstractions.SourceGeneration;
using Microsoft.CodeAnalysis;

namespace CQRSharp.Generators.Types;

public sealed partial class CqrsSourceGenerator
{
    /// <summary>
    ///     Traverses a type's hierarchy to find all unique interfaces it implements, including interfaces of its base types.
    /// </summary>
    /// <param name="typeSymbol">The type symbol to inspect.</param>
    /// <returns>An enumeration of all unique implemented interfaces.</returns>
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
        var commandHandlerSymbols = new[]
        {
            compilation.GetTypeByMetadataName(TypeStrings.ICommandHandler1),
            compilation.GetTypeByMetadataName(TypeStrings.ICommandHandler2)
        };

        var queryHandlerSymbols = new[]
        {
            compilation.GetTypeByMetadataName(TypeStrings.IQueryHandler2),
            compilation.GetTypeByMetadataName(TypeStrings.IQueryHandler3)
        };

        var notificationHandlerSymbol = compilation.GetTypeByMetadataName(TypeStrings.INotificationHandler);
        var requestValidatorSymbol = compilation.GetTypeByMetadataName(TypeStrings.IRequestValidator1);
        var pipelineBehaviorSymbol = compilation.GetTypeByMetadataName(TypeStrings.IPipelineBehavior);

        return commandHandlerSymbols
            .Concat(queryHandlerSymbols)
            .Concat([notificationHandlerSymbol, requestValidatorSymbol, pipelineBehaviorSymbol])
            .Where(s => s is not null)
            .Cast<INamedTypeSymbol>();
    }

    private static ITypeSymbol? GetRequestContextType(ITypeSymbol requestTypeSymbol, Compilation compilation)
    {
        var requestBaseSymbol = compilation.GetTypeByMetadataName(TypeStrings.RequestBaseGeneric);
        if (requestBaseSymbol is null) return null;

        var current = requestTypeSymbol;
        while (current != null)
        {
            if (current is INamedTypeSymbol { IsGenericType: true } namedType && SymbolEqualityComparer.Default.Equals(namedType.OriginalDefinition, requestBaseSymbol))
                return namedType.TypeArguments[0];

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
        const string propertySensitivityFullyQualifiedName = $"global::{TypeStrings.PropertySensitivity}";

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
                    _ => value?.ToString() ?? "null"
                };
            case TypedConstantKind.Enum:
                if (constant.Type is not INamedTypeSymbol enumType) return $"({constant.Type!.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}){value}";
                var member = enumType.GetMembers().OfType<IFieldSymbol>().FirstOrDefault(f => f.ConstantValue is not null && f.ConstantValue.Equals(value));
                return member is not null
                    ? $"{enumType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}.{member.Name}"
                    : $"({enumType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}){value}";
            case TypedConstantKind.Type:
                return $"typeof({((ITypeSymbol)value!).ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)})";
            case TypedConstantKind.Error:
            case TypedConstantKind.Array:
            default:
                return "null";
        }
    }

    private static bool InheritsOrImplements(ITypeSymbol type, ITypeSymbol baseType)
    {
        return GetInterfacesAndBaseInterfaces(type).Any(i => SymbolEqualityComparer.Default.Equals(i.OriginalDefinition, baseType.OriginalDefinition)) ||
               (type.BaseType != null && InheritsOrImplements(type.BaseType, baseType));
    }
}
