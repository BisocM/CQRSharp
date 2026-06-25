using System.Collections.Immutable;
using CQRSharp.Shared;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace CQRSharp.Analyzers;

/// <summary>
///     CQRA001: flags a handler whose context type argument does not match the context type its request declares via
///     <c>RequestBase&lt;TContext&gt;</c>. The mismatch is silent at runtime (the request carries its own context), so
///     it is usually an oversight.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class HandlerContextMismatchAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(CqrsDiagnostics.HandlerContextMismatch);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        context.RegisterCompilationStartAction(start =>
        {
            var known = CqrsKnownSymbols.For(start.Compilation);
            var commandHandler = known.ICommandHandler2;
            var queryHandler = known.IQueryHandler3;
            var streamHandler = known.IStreamRequestHandler3;
            var requestBase = known.RequestBaseGeneric;
            if (requestBase is null || (commandHandler is null && queryHandler is null && streamHandler is null)) return;

            start.RegisterSymbolAction(
                ctx => Analyze(ctx, commandHandler, queryHandler, streamHandler, requestBase),
                SymbolKind.NamedType);
        });
    }

    private static void Analyze(
        SymbolAnalysisContext context,
        INamedTypeSymbol? commandHandler,
        INamedTypeSymbol? queryHandler,
        INamedTypeSymbol? streamHandler,
        INamedTypeSymbol requestBase)
    {
        var type = (INamedTypeSymbol)context.Symbol;
        if (type.TypeKind != TypeKind.Class || type.IsAbstract) return;

        foreach (var iface in type.AllInterfaces)
        {
            if (!iface.IsGenericType) continue;
            var def = iface.OriginalDefinition;

            ITypeSymbol? requestType;
            ITypeSymbol? handlerContext;

            if (commandHandler is not null && SymbolEqualityComparer.Default.Equals(def, commandHandler))
            {
                requestType = iface.TypeArguments[0];
                handlerContext = iface.TypeArguments[1];
            }
            else if (queryHandler is not null && SymbolEqualityComparer.Default.Equals(def, queryHandler))
            {
                requestType = iface.TypeArguments[0];
                handlerContext = iface.TypeArguments[2];
            }
            else if (streamHandler is not null && SymbolEqualityComparer.Default.Equals(def, streamHandler))
            {
                requestType = iface.TypeArguments[0];
                handlerContext = iface.TypeArguments[2];
            }
            else
            {
                continue;
            }

            var declaredContext = GetDeclaredContext(requestType, requestBase);
            if (declaredContext is null) continue; // request does not declare a context via RequestBase<TContext>
            if (SymbolEqualityComparer.Default.Equals(handlerContext, declaredContext)) continue;

            var location = type.Locations.Length > 0 ? type.Locations[0] : Location.None;
            context.ReportDiagnostic(Diagnostic.Create(
                CqrsDiagnostics.HandlerContextMismatch,
                location,
                type.Name,
                handlerContext.Name,
                requestType.Name,
                declaredContext.Name));
        }
    }

    private static ITypeSymbol? GetDeclaredContext(ITypeSymbol requestType, INamedTypeSymbol requestBase)
    {
        for (var current = requestType as INamedTypeSymbol; current is not null; current = current.BaseType)
        {
            if (current.IsGenericType && SymbolEqualityComparer.Default.Equals(current.OriginalDefinition, requestBase))
                return current.TypeArguments[0];
        }

        return null;
    }
}
