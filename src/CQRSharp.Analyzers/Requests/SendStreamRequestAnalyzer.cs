using System.Collections.Immutable;
using System.Linq;
using CQRSharp.Shared;
using Microsoft.CodeAnalysis;
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
        // The request is the first parameter, wherever named arguments put it in the call.
        var requestArgument = invocation.Arguments.FirstOrDefault(a => a.Parameter?.Ordinal == 0);
        if (requestArgument is null) return;

        // The argument value is wrapped in the implicit conversion to the parameter type (IRequest<...>); unwrap it to
        // inspect the actual request type the caller supplied.
        var argValue = requestArgument.Value;
        while (argValue is IConversionOperation conversion)
            argValue = conversion.Operand;

        var argType = argValue.Type;
        if (argType is null || !argType.Implements(streamMarker)) return;

        // On the 'Send' name, so the squiggle is precise and the code fix can find the call from it.
        ctx.ReportDiagnostic(Diagnostic.Create(CqrsDiagnostics.SendStreamRequest, invocation.NameLocation(), argType.Name));
    }
}