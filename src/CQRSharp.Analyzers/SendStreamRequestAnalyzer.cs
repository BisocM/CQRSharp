using System.Collections.Immutable;
using CQRSharp.Shared;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace CQRSharp.Analyzers;

/// <summary>
///     CQRA004: flags <c>ICqrsDispatcher.Send(...)</c> calls whose argument is an <c>IStreamRequest</c>. Such a call
///     compiles (a stream request is an <c>IRequest&lt;IAsyncEnumerable&lt;T&gt;&gt;</c>) but throws at runtime; the
///     request must be dispatched with <c>Stream(...)</c>.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class SendStreamRequestAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(CqrsDiagnostics.SendStreamRequest);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        context.RegisterCompilationStartAction(start =>
        {
            var known = CqrsKnownSymbols.For(start.Compilation);
            var dispatcher = known.Dispatcher;
            var streamMarker = known.IStreamRequestMarker;
            if (dispatcher is null || streamMarker is null) return;

            start.RegisterOperationAction(ctx => Analyze(ctx, dispatcher, streamMarker), OperationKind.Invocation);
        });
    }

    private static void Analyze(OperationAnalysisContext ctx, INamedTypeSymbol dispatcher, INamedTypeSymbol streamMarker)
    {
        var invocation = (IInvocationOperation)ctx.Operation;
        var method = invocation.TargetMethod;

        if (method.Name != "Send") return;
        if (!SymbolEqualityComparer.Default.Equals(method.ContainingType, dispatcher)) return;
        if (invocation.Arguments.Length == 0) return;

        // The argument value is wrapped in the implicit conversion to the parameter type (IRequest<...>); unwrap it to
        // inspect the actual request type the caller supplied.
        var argValue = invocation.Arguments[0].Value;
        while (argValue is IConversionOperation conversion)
            argValue = conversion.Operand;

        var argType = argValue.Type;
        if (argType is null || !Implements(argType, streamMarker)) return;

        // Prefer reporting on the 'Send' member name so the squiggle is precise and the code-fix can target it.
        var location = invocation.Syntax is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax memberAccess }
            ? memberAccess.Name.GetLocation()
            : invocation.Syntax.GetLocation();

        ctx.ReportDiagnostic(Diagnostic.Create(CqrsDiagnostics.SendStreamRequest, location, argType.Name));
    }

    private static bool Implements(ITypeSymbol type, INamedTypeSymbol iface)
    {
        if (SymbolEqualityComparer.Default.Equals(type.OriginalDefinition, iface)) return true;
        foreach (var implemented in type.AllInterfaces)
            if (SymbolEqualityComparer.Default.Equals(implemented.OriginalDefinition, iface))
                return true;
        return false;
    }
}