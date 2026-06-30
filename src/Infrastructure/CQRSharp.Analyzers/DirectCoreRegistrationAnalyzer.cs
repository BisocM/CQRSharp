using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace CQRSharp.Analyzers;

/// <summary>
///     CQRA014: flags a direct call to the low-level <c>AddCqrs(...)</c> core registration in user code. It registers
///     the dispatcher but not the source-generated handler routing, so the first dispatch throws at runtime. The only
///     legitimate callers are inside the framework (the fluent builder and the generated bootstrap), which are not user
///     source — so any call the analyzer sees in non-generated code is a mistake. The fix is <c>AddCqrsGenerated</c>.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class DirectCoreRegistrationAnalyzer : DiagnosticAnalyzer
{
    // Resolve the call by the resolved method's containing type (not by "is it followed by AddGenerated"): the fluent
    // AddCqrsGenerated(b => ...) overload is a different method and must not be flagged.
    private const string ExtensionsMetadataName = "CQRSharp.Core.Extensions.DependencyInjectionExtensions";

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(CqrsDiagnostics.DirectCoreRegistration);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            var extensions = start.Compilation.GetTypeByMetadataName(ExtensionsMetadataName);
            if (extensions is null) return;
            start.RegisterOperationAction(ctx => Analyze(ctx, extensions), OperationKind.Invocation);
        });
    }

    private static void Analyze(OperationAnalysisContext ctx, INamedTypeSymbol extensions)
    {
        var invocation = (IInvocationOperation)ctx.Operation;
        var method = invocation.TargetMethod;
        if (method.Name != "AddCqrs") return;
        if (!SymbolEqualityComparer.Default.Equals(method.ContainingType, extensions)) return;

        var location = invocation.Syntax is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax member }
            ? member.Name.GetLocation()
            : invocation.Syntax.GetLocation();
        ctx.ReportDiagnostic(Diagnostic.Create(CqrsDiagnostics.DirectCoreRegistration, location));
    }
}
