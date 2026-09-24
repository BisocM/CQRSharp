using System.Collections.Immutable;
using System.Linq;
using CQRSharp.Shared;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace CQRSharp.Analyzers;

/// <summary>
///     CQRA005: a <c>[PipelineExemption(typeof(T))]</c> that can never take effect. At runtime an exemption is read from
///     the request being dispatched and matches a behavior when <c>T</c> is that behavior's concrete type or its
///     open-generic definition, so it is dead when <c>T</c> is not a pipeline behavior, is abstract or an interface,
///     never runs for the request (closed over another request or result, or of the other pipeline kind), or when the
///     attribute sits on a type that is not a request. CQRA008: a closed form that does exempt the behavior can be
///     written as <c>typeof(Behavior&lt;,&gt;)</c>, which means the same on a request no other request derives from.
/// </summary>
/// <remarks>
///     The attribute is inherited, as the generator reads it: a request derived from the one it sits on is exempted too.
///     So an exemption on a class another request can derive from (any class that is not sealed) is judged against that
///     class only where no derived request could make it live, and its closed form is not offered the open one, which
///     would also exempt the behavior closed over every derived request. On an abstract class the derived requests alone
///     decide what it applies to.
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PipelineExemptionAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(CqrsDiagnostics.PipelineExemptionHasNoEffect, CqrsDiagnostics.PipelineExemptionClosedGeneric);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        context.RegisterCompilationStartAction(start =>
        {
            var known = CqrsKnownSymbols.For(start.Compilation);
            var exemption = known.PipelineExemptionAttribute;
            if (exemption is null || (known.IPipelineBehavior is null && known.IStreamPipelineBehavior is null)) return;

            start.RegisterSyntaxNodeAction(ctx => Analyze(ctx, exemption, known), SyntaxKind.Attribute);
        });
    }

    private static void Analyze(SyntaxNodeAnalysisContext ctx, INamedTypeSymbol exemption, CqrsKnownSymbols known)
    {
        var attribute = (AttributeSyntax)ctx.Node;
        if (attribute.ArgumentList is null) return;
        if (ctx.SemanticModel.GetSymbolInfo(attribute, ctx.CancellationToken).Symbol is not IMethodSymbol constructor) return;
        if (!SymbolEqualityComparer.Default.Equals(constructor.ContainingType, exemption)) return;
        if (attribute.Parent?.Parent is not TypeDeclarationSyntax declaration) return;
        if (ctx.SemanticModel.GetDeclaredSymbol(declaration, ctx.CancellationToken) is not { } target) return;

        var service = known.PipelineBehaviorServiceOf(target);
        var targetProblem = service is null ? NotARequest(target, known) : null;

        foreach (var argument in attribute.ArgumentList.Arguments)
        {
            if (argument.Expression is not TypeOfExpressionSyntax typeOf) continue;
            if (ctx.SemanticModel.GetTypeInfo(typeOf.Type, ctx.CancellationToken).Type is not INamedTypeSymbol exempted) continue;

            var shown = exempted.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
            var reason = Problem(exempted, target, service, targetProblem, known, ctx.Compilation);
            if (reason is not null)
            {
                ctx.ReportDiagnostic(Diagnostic.Create(CqrsDiagnostics.PipelineExemptionHasNoEffect, typeOf.GetLocation(), shown, reason));
                continue;
            }

            // A closed form that does exempt the behavior for this request names the same behavior as the open form.
            // Only for a type generic in its own right and declared outside any generic type: IsGenericType is also true
            // for a non-generic type nested in a generic one, and an open form of a nested type would have to unbind its
            // containers too, which exempts more than the attribute names.
            if (service is not null && IsFinal(target) &&
                exempted.TypeArguments.Length > 0 && !exempted.IsUnboundGenericType && !HasGenericContainer(exempted))
                ctx.ReportDiagnostic(Diagnostic.Create(
                    CqrsDiagnostics.PipelineExemptionClosedGeneric, typeOf.GetLocation(), shown, OpenForm(exempted)));
        }
    }

    // Why the exemption can never match a behavior that runs, or null when it can.
    private static string? Problem(INamedTypeSymbol exempted, INamedTypeSymbol target, INamedTypeSymbol? service, string? targetProblem,
        CqrsKnownSymbols known, Compilation compilation)
    {
        // typeof(Behavior<,>) is an unbound generic whose interfaces are empty; its definition carries them.
        var definition = exempted.IsUnboundGenericType ? exempted.OriginalDefinition : exempted;
        var shown = exempted.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);

        if (!ImplementsAny(definition, known.IPipelineBehavior, known.IStreamPipelineBehavior))
            return $"'{shown}' is not a pipeline behavior";

        if (definition.IsAbstract || definition.TypeKind == TypeKind.Interface)
            return $"'{shown}' is {(definition.TypeKind == TypeKind.Interface ? "an interface" : "abstract")}, and an exemption matches the concrete behavior type that runs";

        if (targetProblem is not null) return targetProblem;

        // Under an abstract class the derived requests decide which behaviors run.
        if (service is null || target.IsAbstract) return null;

        if (exempted.IsUnboundGenericType)
            return ImplementsAny(definition, service.OriginalDefinition, null)
                ? null
                : $"'{shown}' never runs for '{target.Name}', which is {(IsStream(service, known) ? "a stream request" : "not a stream request")}";

        // A closed or non-generic behavior runs for the request only if it can be resolved as the request's behavior service.
        if (compilation.ClassifyConversion(exempted, service) is { IsImplicit: true } conversion && (conversion.IsIdentity || conversion.IsReference))
            return null;

        // Inherited by a request derived from this one, it is live where it runs for that request.
        if (!IsFinal(target) && RunsForADerivedRequest(exempted, target, service))
            return null;

        return exempted.IsGenericType && !HasGenericContainer(exempted) && ImplementsAny(definition.OriginalDefinition, service.OriginalDefinition, null)
            ? $"'{shown}' is closed over other type arguments, so it never runs for '{target.Name}'; exempt 'typeof({OpenForm(exempted)})' instead"
            : $"'{shown}' never runs for '{target.Name}'";
    }

    // Exemptions are read from the request being dispatched. A class that is not a request only matters if a request can
    // derive from it, which a sealed class, a struct, a static class or a handler cannot.
    private static string? NotARequest(INamedTypeSymbol target, CqrsKnownSymbols known)
    {
        if (target.AllInterfaces.Any(known.IsDispatchHandler))
            return $"'{target.Name}' is a handler, and exemptions are read from the request being dispatched";

        return target.IsSealed || target.IsStatic || target.IsValueType
            ? $"'{target.Name}' is not a request, and exemptions are read from the request being dispatched"
            : null;
    }

    // Whether no other request can derive from the target, and so inherit an exemption declared on it.
    private static bool IsFinal(INamedTypeSymbol target) => target.IsSealed || target.IsValueType;

    // Whether the behavior is the behavior service of a request type derived from the target: closed over a derived
    // request, or a non-generic behavior declared for one.
    private static bool RunsForADerivedRequest(INamedTypeSymbol exempted, INamedTypeSymbol target, INamedTypeSymbol service)
    {
        foreach (var iface in exempted.AllInterfaces)
        {
            if (!SymbolEqualityComparer.Default.Equals(iface.OriginalDefinition, service.OriginalDefinition)) continue;
            for (var request = iface.TypeArguments[0] as INamedTypeSymbol; request is not null; request = request.BaseType)
                if (SymbolEqualityComparer.Default.Equals(request, target))
                    return true;
        }

        return false;
    }

    private static bool IsStream(INamedTypeSymbol service, CqrsKnownSymbols known)
        => SymbolEqualityComparer.Default.Equals(service.OriginalDefinition, known.IStreamPipelineBehavior);

    private static string OpenForm(INamedTypeSymbol type) => type.Name + "<" + new string(',', type.TypeArguments.Length - 1) + ">";

    private static bool HasGenericContainer(INamedTypeSymbol type)
    {
        for (var container = type.ContainingType; container is not null; container = container.ContainingType)
            if (container.TypeArguments.Length > 0)
                return true;
        return false;
    }

    private static bool ImplementsAny(INamedTypeSymbol type, INamedTypeSymbol? first, INamedTypeSymbol? second)
    {
        foreach (var iface in type.AllInterfaces)
        {
            var definition = iface.OriginalDefinition;
            if (first is not null && SymbolEqualityComparer.Default.Equals(definition, first)) return true;
            if (second is not null && SymbolEqualityComparer.Default.Equals(definition, second)) return true;
        }

        return false;
    }
}
