using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using CQRSharp.Shared;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CQRSharp.Generators.CqrsSourceGenerator;

public sealed partial class CqrsSourceGenerator
{
    // ============================================================================================================
    // Symbol helpers used only during extraction.
    // ============================================================================================================

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

    private static bool InheritsOrImplements(ITypeSymbol type, ITypeSymbol baseType)
    {
        if (SymbolEqualityComparer.Default.Equals(type.OriginalDefinition, baseType.OriginalDefinition))
            return true;

        return type.AllInterfaces.Any(i => SymbolEqualityComparer.Default.Equals(i.OriginalDefinition, baseType.OriginalDefinition)) ||
               (type.BaseType != null && InheritsOrImplements(type.BaseType, baseType));
    }

    private static string EscapeIdentifier(string identifier)
        => SyntaxFacts.GetKeywordKind(identifier) != SyntaxKind.None ? "@" + identifier : identifier;

    // The outermost part of a type generated code cannot name - the private DTO of a List<PrivateDto>, not the list.
    private static ITypeSymbol InaccessiblePartOf(ITypeSymbol type, Compilation compilation)
    {
        switch (type)
        {
            case IArrayTypeSymbol array when !GeneratedCodeAccessibility.IsAccessible(array.ElementType, compilation):
                return InaccessiblePartOf(array.ElementType, compilation);
            case INamedTypeSymbol { IsGenericType: true } named:
                var definitionAccessible = GeneratedCodeAccessibility.IsAccessible(named.OriginalDefinition, compilation);
                if (definitionAccessible)
                    foreach (var argument in named.TypeArguments)
                        if (argument is not ITypeParameterSymbol && !GeneratedCodeAccessibility.IsAccessible(argument, compilation))
                            return InaccessiblePartOf(argument, compilation);
                return type;
            default:
                return type;
        }
    }

    // ============================================================================================================
    // Request attributes (pre-/post-handler interceptors and pipeline exemptions).
    // ============================================================================================================

    /// <summary>
    ///     The attributes of <paramref name="requestType" /> that implement <paramref name="attributeInterface" />, the way
    ///     <c>Type.GetCustomAttributes(inherit: true)</c> reads them: those declared on the type, then those of each base
    ///     class whose <c>AttributeUsage.Inherited</c> is true, the most derived one only when <c>AllowMultiple</c> is false.
    ///     An attribute generated code cannot rebuild is left out and described in <paramref name="skipped" /> (CQRGEN016).
    /// </summary>
    private static AttributeModel[] BuildAttributeModels(
        ITypeSymbol requestType,
        INamedTypeSymbol? attributeInterface,
        Compilation compilation,
        CqrsKnownSymbols known,
        List<string> skipped)
    {
        if (attributeInterface is null || requestType is not INamedTypeSymbol request) return Array.Empty<AttributeModel>();

        var models = new List<AttributeModel>();
        var seenSingle = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        for (var current = request; current is not null && current.SpecialType != SpecialType.System_Object; current = current.BaseType)
        {
            var declaredHere = SymbolEqualityComparer.Default.Equals(current, request);
            foreach (var attribute in current.GetAttributes())
            {
                if (attribute.AttributeClass is not { TypeKind: not TypeKind.Error } attributeClass) continue;
                if (!InheritsOrImplements(attributeClass, attributeInterface)) continue;

                var (inherited, allowMultiple) = UsageOf(attributeClass, known);
                if (!declaredHere && !inherited) continue;
                if (!allowMultiple && !seenSingle.Add(attributeClass)) continue;

                if (TryBuildAttributeModel(attribute, attributeClass, compilation, out var model, out var failure))
                    models.Add(model!);
                else if (failure is not null)
                    skipped.Add($"[{attributeClass.ToDisplayString()}] on '{current.ToDisplayString()}' ({failure})");
            }
        }

        return models.ToArray();
    }

    // AttributeUsage is itself inherited, so an attribute class without one takes its base class's. The defaults are
    // those of AttributeUsageAttribute: inherited, single use.
    private static (bool Inherited, bool AllowMultiple) UsageOf(INamedTypeSymbol attributeClass, CqrsKnownSymbols known)
    {
        var usage = known.AttributeUsageAttribute;
        if (usage is not null)
            for (var current = attributeClass; current is not null; current = current.BaseType)
            {
                var declared = current.GetAttributes().FirstOrDefault(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, usage));
                if (declared is null) continue;

                var inherited = true;
                var allowMultiple = false;
                foreach (var named in declared.NamedArguments)
                    if (named.Value.Value is bool flag)
                    {
                        if (named.Key == "Inherited") inherited = flag;
                        else if (named.Key == "AllowMultiple") allowMultiple = flag;
                    }

                return (inherited, allowMultiple);
            }

        return (true, false);
    }

    private static bool TryBuildAttributeModel(
        AttributeData attribute,
        INamedTypeSymbol attributeClass,
        Compilation compilation,
        out AttributeModel? model,
        out string? failure)
    {
        model = null;
        failure = null;

        if (!GeneratedCodeAccessibility.IsAccessible(attributeClass, compilation))
        {
            failure = "the attribute type is not accessible to generated code";
            return false;
        }

        if (attribute.AttributeConstructor is { } constructor && !GeneratedCodeAccessibility.IsAccessible(constructor, compilation))
        {
            failure = "its constructor is not accessible to generated code";
            return false;
        }

        var constructorArgs = new string[attribute.ConstructorArguments.Length];
        for (var i = 0; i < constructorArgs.Length; i++)
            if (!TryRenderTypedConstant(attribute.ConstructorArguments[i], compilation, out constructorArgs[i], out failure))
                return false;

        // [Audit(Category = "billing")]: re-applied through an object initializer on the rebuilt instance.
        var namedArgs = new string[attribute.NamedArguments.Length];
        for (var i = 0; i < namedArgs.Length; i++)
        {
            var named = attribute.NamedArguments[i];
            if (!TryRenderTypedConstant(named.Value, compilation, out var rendered, out failure))
                return false;
            namedArgs[i] = $"{EscapeIdentifier(named.Key)} = {rendered}";
        }

        model = new AttributeModel(
            attributeClass.ToDisplayString(Fq),
            new EquatableArray<string>(constructorArgs),
            new EquatableArray<string>(namedArgs));
        return true;
    }

    /// <summary>
    ///     Renders an attribute argument as a C# expression that compiles to the same value in generated code. Fails (with
    ///     a reason) for a type generated code cannot name, and without one for an argument that is itself a compile error.
    /// </summary>
    private static bool TryRenderTypedConstant(TypedConstant constant, Compilation compilation, out string rendered, out string? failure)
    {
        rendered = "null";
        failure = null;

        if (constant.Kind == TypedConstantKind.Error) return false;

        if (constant.Type is { } constantType && !GeneratedCodeAccessibility.IsAccessible(constantType, compilation))
        {
            failure = $"an argument's type '{constantType.ToDisplayString()}' is not accessible to generated code";
            return false;
        }

        // Typed, so an attribute with a string and an object overload still binds to the constructor the source chose.
        if (constant.IsNull)
        {
            rendered = constant.Type is null ? "null" : $"({constant.Type.ToDisplayString(Fq)})null";
            return true;
        }

        switch (constant.Kind)
        {
            case TypedConstantKind.Primitive:
                rendered = RenderConstant(constant.Value!, constant.Type!);
                return true;
            case TypedConstantKind.Enum:
                rendered = RenderConstant(constant.Value!, constant.Type!);
                return true;
            case TypedConstantKind.Type:
                var type = (ITypeSymbol)constant.Value!;
                // typeof(Behavior<,>): an unbound generic's type arguments are placeholders; its definition is what is named.
                var named = type is INamedTypeSymbol { IsUnboundGenericType: true } unbound ? unbound.OriginalDefinition : type;
                if (!GeneratedCodeAccessibility.IsAccessible(named, compilation))
                {
                    failure = $"typeof({type.ToDisplayString()}) names a type that is not accessible to generated code";
                    return false;
                }

                rendered = $"typeof({type.ToDisplayString(Fq)})";
                return true;
            case TypedConstantKind.Array:
                // Includes a params argument: [RequireRoles("a", "b")] arrives as one string[] constant.
                var elements = new string[constant.Values.Length];
                for (var i = 0; i < elements.Length; i++)
                    if (!TryRenderTypedConstant(constant.Values[i], compilation, out elements[i], out failure))
                        return false;
                var elementType = ((IArrayTypeSymbol)constant.Type!).ElementType.ToDisplayString(Fq);
                rendered = $"new {elementType}[] {{ {string.Join(", ", elements)} }}";
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    ///     A compile-time constant of <paramref name="type" /> (a primitive, a string, an enum value) as a C# expression:
    ///     culture-invariant, fully escaped, cast to its exact type, parenthesized so a negative value never reads as a
    ///     subtraction.
    /// </summary>
    private static string RenderConstant(object value, ITypeSymbol type)
    {
        if (type.TypeKind == TypeKind.Enum && type is INamedTypeSymbol enumType)
        {
            var enumName = enumType.ToDisplayString(Fq);
            var member = enumType.GetMembers().OfType<IFieldSymbol>()
                .FirstOrDefault(f => f.HasConstantValue && Equals(f.ConstantValue, value));
            return member is not null
                ? $"{enumName}.{EscapeIdentifier(member.Name)}"
                : $"(({enumName})({SymbolDisplay.FormatPrimitive(value, quoteStrings: false, useHexadecimalNumbers: false)}))";
        }

        var typeName = type.ToDisplayString(Fq);
        switch (value)
        {
            case string or char or bool or int:
                return SymbolDisplay.FormatPrimitive(value, quoteStrings: true, useHexadecimalNumbers: false);
            case double d when double.IsNaN(d):
                return "double.NaN";
            case double d when double.IsInfinity(d):
                return d > 0 ? "double.PositiveInfinity" : "double.NegativeInfinity";
            case float f when float.IsNaN(f):
                return "float.NaN";
            case float f when float.IsInfinity(f):
                return f > 0 ? "float.PositiveInfinity" : "float.NegativeInfinity";
            // A real literal is suffixed with its own type, so it is never rounded through double first.
            case float:
                return $"(({typeName})({SymbolDisplay.FormatPrimitive(value, quoteStrings: false, useHexadecimalNumbers: false)}F))";
            case double:
                return $"(({typeName})({SymbolDisplay.FormatPrimitive(value, quoteStrings: false, useHexadecimalNumbers: false)}D))";
            case decimal m:
                return $"(({typeName})({m.ToString(CultureInfo.InvariantCulture)}M))";
            default:
                // An integer literal takes the smallest type that holds it; the cast gives it its exact one.
                return $"(({typeName})({SymbolDisplay.FormatPrimitive(value, quoteStrings: true, useHexadecimalNumbers: false)}))";
        }
    }

    // ============================================================================================================
    // Names.
    // ============================================================================================================

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

    // The runtime's Type.FullName of a type definition: namespace-qualified, nested types joined with '+', generic
    // arity as `N. What the runtime compares a type generated code cannot name against.
    private static string RuntimeNameOf(INamedTypeSymbol type)
    {
        var parts = new Stack<string>();
        for (var current = type.OriginalDefinition; current is not null; current = current.ContainingType)
            parts.Push(current.MetadataName);

        var name = string.Join("+", parts);
        var ns = type.ContainingNamespace is { IsGlobalNamespace: false } containing ? containing.ToDisplayString() : null;
        return ns is null ? name : ns + "." + name;
    }

    private static string CreateIdentifierSuffix(string text, string prefix)
    {
        var hash = CqrsKnownSymbols.StableHash(text);

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
        sb.Append(hash.ToString("X8", CultureInfo.InvariantCulture));
        return sb.ToString();
    }
}
