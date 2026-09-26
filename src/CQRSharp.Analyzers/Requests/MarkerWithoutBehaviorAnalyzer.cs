using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using CQRSharp.Shared;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace CQRSharp.Analyzers;

/// <summary>
///     CQRA018 / CQRA019: the application handles a request that implements <c>IIdempotentRequest</c> (or
///     <c>IRetryableRequest</c>), and its CQRSharp configuration, all of it in view, never calls <c>UseIdempotency</c> (or
///     <c>UseResilience</c>), so the behavior the marker depends on is not registered. At runtime the first dispatch of
///     such a request fails with CQRCONF005 (or logs CQRCONF006); this reports it at build, at the registration.
/// </summary>
/// <remarks>
///     Reported only against a configuration <see cref="VisibleConfigurationCollector" /> proves complete: anything the
///     analyzer cannot follow (a helper method, a condition, a second registration, a module in another assembly, a
///     configuration passed as a variable) may register the behavior, so nothing is reported then. A behavior that is
///     not registered is missing whether or not the request exempts it, as the runtime rule has it.
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class MarkerWithoutBehaviorAnalyzer : DiagnosticAnalyzer
{
    /// <summary>The property that names the builder verb the code fix adds.</summary>
    internal const string VerbProperty = "Verb";

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(CqrsDiagnostics.IdempotentRequestWithoutIdempotency, CqrsDiagnostics.RetryableRequestWithoutResilience);

    public override void Initialize(AnalysisContext context)
    {
        // Generated code is analyzed too: another generator could register CQRSharp, which would take the configuration out
        // of view. The collector leaves out what CQRSharp's own generator emits.
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(OnCompilationStart);
    }

    private static void OnCompilationStart(CompilationStartAnalysisContext context)
    {
        var compilation = context.Compilation;
        var known = CqrsKnownSymbols.For(compilation);
        var idempotent = known.IIdempotentRequest;
        var retryable = compilation.GetTypeByMetadataName("CQRSharp.IRetryableRequest");
        if (idempotent is null || retryable is null) return;

        var collector = VisibleConfigurationCollector.Start(context);
        if (collector is null) return;

        var gate = new object();
        var handledRequests = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
        context.RegisterSymbolAction(symbolContext =>
        {
            var type = (INamedTypeSymbol)symbolContext.Symbol;
            if (type is not { TypeKind: TypeKind.Class, IsAbstract: false, IsGenericType: false } || CqrsRegistrationCalls.IsInGeneratedCode(type)) return;

            foreach (var iface in type.AllInterfaces)
                if (known.IsDispatchHandler(iface) && !SymbolEqualityComparer.Default.Equals(iface.OriginalDefinition, known.INotificationHandler))
                    lock (gate) handledRequests.Add(iface.TypeArguments[0]);
        }, SymbolKind.NamedType);

        context.RegisterCompilationEndAction(endContext =>
        {
            if (collector.Complete() is not { } configuration) return;

            // The generator's markers for this compilation cover the handlers declared in generated code.
            foreach (var request in HandledRequestMarkers(compilation.Assembly, known))
                handledRequests.Add(request);

            Report(endContext, configuration, handledRequests, idempotent, "UseIdempotency", CqrsDiagnostics.IdempotentRequestWithoutIdempotency);
            Report(endContext, configuration, handledRequests, retryable, "UseResilience", CqrsDiagnostics.RetryableRequestWithoutResilience);
        });
    }

    private static void Report(
        CompilationAnalysisContext context,
        VisibleConfiguration configuration,
        HashSet<ITypeSymbol> handledRequests,
        INamedTypeSymbol marker,
        string verb,
        DiagnosticDescriptor descriptor)
    {
        if (configuration.Calls(verb)) return;

        var requests = handledRequests
            .Where(r => r.Implements(marker))
            .Select(r => r.ToDisplayString())
            .Distinct()
            .OrderBy(n => n, System.StringComparer.Ordinal)
            .ToArray();
        if (requests.Length == 0) return;

        context.ReportDiagnostic(Diagnostic.Create(
            descriptor,
            configuration.Registration.NameLocation(),
            ImmutableDictionary<string, string?>.Empty.Add(VerbProperty, verb),
            Names(requests)));
    }

    // 'A', 'B' and 'C'; past four, the first three and how many more.
    private static string Names(string[] names)
    {
        const int shown = 3;
        var quoted = names.Select(n => "'" + n + "'").ToArray();
        if (quoted.Length == 1) return quoted[0];
        if (quoted.Length <= shown + 1) return string.Join(", ", quoted.Take(quoted.Length - 1)) + " and " + quoted[quoted.Length - 1];
        return string.Join(", ", quoted.Take(shown)) + $" and {quoted.Length - shown} more";
    }

    private static IEnumerable<ITypeSymbol> HandledRequestMarkers(IAssemblySymbol assembly, CqrsKnownSymbols known)
    {
        if (known.CqrsHandledRequestAttribute is not { } marker) yield break;

        foreach (var attribute in assembly.GetAttributes())
            if (SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, marker) &&
                attribute.ConstructorArguments.Length == 1 &&
                attribute.ConstructorArguments[0] is { Kind: TypedConstantKind.Type, Value: ITypeSymbol request })
                yield return request;
    }
}
