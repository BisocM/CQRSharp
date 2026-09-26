using CQRSharp.Shared;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace CQRSharp.Analyzers;

/// <summary>
///     How the analyzers recognize a CQRSharp registration and read the builder configuration it is called with. Calls are
///     recognized by the method they bind to, never by name alone.
/// </summary>
internal static class CqrsRegistrationCalls
{
    /// <summary>
    ///     Whether <paramref name="definition" /> (the method a call binds to, its reduced form undone) is a CQRSharp
    ///     registration: the core <c>AddCqrs</c>, or one of the generated <c>AddCqrsGenerated</c> entry points.
    /// </summary>
    public static bool IsRegistration(IMethodSymbol definition, INamedTypeSymbol coreExtensions)
    {
        var containing = definition.ContainingType;
        if (SymbolEqualityComparer.Default.Equals(containing, coreExtensions)) return definition.Name == "AddCqrs";

        // The generated entry points, emitted into the consuming assembly itself (namespace CQRSharp, or its module namespace).
        return definition.Name == "AddCqrsGenerated" && IsGeneratedBootstrap(containing);
    }

    /// <summary>Whether <paramref name="type" /> is a bootstrap the CQRSharp source generator emitted.</summary>
    public static bool IsGeneratedBootstrap(INamedTypeSymbol? type)
        => type is { Name: CqrsKnownSymbols.BootstrapTypeName } && IsGeneratedNamespace(type.ContainingNamespace.ToDisplayString());

    /// <summary>
    ///     Whether <paramref name="symbol" /> is part of the code the CQRSharp source generator emits: its bootstrap, or
    ///     anything in a <c>CQRSharp.Generated</c> module namespace.
    /// </summary>
    public static bool IsInGeneratedCode(ISymbol? symbol)
    {
        for (var type = symbol as INamedTypeSymbol ?? symbol?.ContainingType; type is not null; type = type.ContainingType)
        {
            if (IsGeneratedBootstrap(type)) return true;
            var ns = type.ContainingNamespace.ToDisplayString();
            if (ns == "CQRSharp.Generated" || ns.StartsWith("CQRSharp.Generated.", System.StringComparison.Ordinal)) return true;
        }

        return false;
    }

    /// <summary>The <c>Action&lt;ICqrsBuilder&gt;</c> a registration is called with; null for a registration without one.</summary>
    public static IOperation? BuilderConfiguration(IInvocationOperation invocation, INamedTypeSymbol builderInterface)
    {
        foreach (var argument in invocation.Arguments)
            if (argument.Parameter?.Type is INamedTypeSymbol { IsGenericType: true, TypeArguments.Length: 1 } parameterType &&
                SymbolEqualityComparer.Default.Equals(parameterType.TypeArguments[0], builderInterface))
                return argument.Value;

        return null;
    }

    /// <summary>
    ///     The builder a fluent call is made on: the instance of a call, or the first argument of an extension method's
    ///     (UseFluentValidation and the store verbs are extensions of the builder); null when <paramref name="operation" />
    ///     is not a call.
    /// </summary>
    public static IOperation? Receiver(IOperation operation)
    {
        operation = WithoutConversions(operation);
        if (operation is not IInvocationOperation call) return null;

        var receiver = call.Instance ??
                       (call.TargetMethod.IsExtensionMethod && call.Arguments.Length > 0 ? call.Arguments[0].Value : null);
        return receiver is null ? null : WithoutConversions(receiver);
    }

    private static IOperation WithoutConversions(IOperation operation)
    {
        while (operation is IConversionOperation conversion) operation = conversion.Operand;
        return operation;
    }

    private static bool IsGeneratedNamespace(string ns)
        => ns == "CQRSharp" || ns.StartsWith("CQRSharp.Generated.", System.StringComparison.Ordinal);
}
