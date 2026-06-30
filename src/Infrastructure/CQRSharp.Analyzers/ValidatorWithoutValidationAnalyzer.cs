using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using CQRSharp.Shared;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace CQRSharp.Analyzers;

/// <summary>
///     CQRA012: a project registers CQRSharp (an <c>AddCqrsGenerated</c>/<c>AddCqrs</c> call is present) and declares an
///     <c>IRequestValidator&lt;T&gt;</c>, but enables no pipeline pack — so the validation behavior never runs and the
///     validator is silently inert. The pack (and with it validation) is active only when a pack verb is used; this is
///     security-relevant because unvalidated input then reaches the handler. Deliberately conservative: it fires only in
///     a compilation that actually wires CQRSharp (a composition root), so a library that merely declares validators and
///     is wired elsewhere is not flagged.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ValidatorWithoutValidationAnalyzer : DiagnosticAnalyzer
{
    // Any of these enables the pipeline pack (and therefore validation) on the builder; a UnitOfWork/RateLimiting/etc.
    // verb flips the same pack flag, so the presence of any one means validation is active and there is nothing to warn.
    private static readonly ImmutableHashSet<string> PackEnablingMethods = ImmutableHashSet.Create(
        "UseValidation", "UsePipelinePack", "AddCqrsPipelinePack", "UseLogging", "UseExceptionHandling",
        "UseRateLimiting", "UseResilience", "UseTimeout", "UseUnitOfWork", "AddValidationBehavior");

    // Presence of one of these marks the compilation as a composition root that actually wires CQRSharp.
    private static readonly ImmutableHashSet<string> RegistrationMethods = ImmutableHashSet.Create(
        "AddCqrsGenerated", "AddGenerated", "AddCqrs");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(CqrsDiagnostics.ValidatorWithoutValidation);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(OnCompilationStart);
    }

    private static void OnCompilationStart(CompilationStartAnalysisContext context)
    {
        var known = CqrsKnownSymbols.For(context.Compilation);
        var validatorInterface = known.IRequestValidator1;
        if (validatorInterface is null) return;

        var packEnabled = new bool[1];
        var registersCqrs = new bool[1];
        var gate = new object();
        var validators = new List<(string validator, string request, Location location)>();

        context.RegisterOperationAction(op =>
        {
            var name = ((IInvocationOperation)op.Operation).TargetMethod.Name;
            if (PackEnablingMethods.Contains(name)) lock (gate) packEnabled[0] = true;
            if (RegistrationMethods.Contains(name)) lock (gate) registersCqrs[0] = true;
        }, OperationKind.Invocation);

        context.RegisterSymbolAction(symbolContext =>
        {
            var type = (INamedTypeSymbol)symbolContext.Symbol;
            if (type.TypeKind != TypeKind.Class || type.IsAbstract) return;

            foreach (var iface in type.AllInterfaces)
            {
                if (!iface.IsGenericType) continue;
                if (!SymbolEqualityComparer.Default.Equals(iface.OriginalDefinition, validatorInterface)) continue;

                var location = type.Locations.FirstOrDefault(l => l.IsInSource);
                if (location is not null)
                    lock (gate) validators.Add((type.Name, iface.TypeArguments[0].Name, location));
                break;
            }
        }, SymbolKind.NamedType);

        context.RegisterCompilationEndAction(endContext =>
        {
            // Only flag a composition root that wires CQRSharp without a pack — avoids false positives on a library that
            // declares validators and is registered elsewhere (where the pack verb actually lives).
            if (packEnabled[0] || !registersCqrs[0]) return;

            foreach (var (validator, request, location) in validators)
                endContext.ReportDiagnostic(Diagnostic.Create(
                    CqrsDiagnostics.ValidatorWithoutValidation, location, validator, request));
        });
    }
}
