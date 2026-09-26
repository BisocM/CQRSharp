using System.Collections.Generic;
using System.Collections.Immutable;
using CQRSharp.Shared;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace CQRSharp.Analyzers;

/// <summary>
///     CQRA020: the application turns the outbox on (<c>UseOutbox</c>) under the generated notification serializer, and
///     one of its notification classes has handlers but no <c>[NotificationName]</c>, so it is never stored in the outbox:
///     every publish of it is delivered in-process. That may be intended (a notification that only matters in this
///     process), so it is a suggestion; at runtime its first publish under the outbox logs CQRCONF003.
/// </summary>
/// <remarks>
///     Reported only against a configuration <see cref="VisibleConfigurationCollector" /> proves complete, and only while
///     nothing in view can choose another serializer (which names what it chooses) or set the outbox options outside the
///     builder. It covers what the runtime rule covers under the generated serializer: a concrete, non-generic class the
///     generated code can name, handled (through its own type, a base type or an interface) by a handler the generator
///     subscribes, declared in this compilation, where the attribute can be added.
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class UnnamedHandledNotificationAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(CqrsDiagnostics.UnnamedHandledNotification);

    public override void Initialize(AnalysisContext context)
    {
        // Generated code is analyzed too: another generator could register CQRSharp or a serializer. The collector leaves
        // out what CQRSharp's own generator emits.
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(OnCompilationStart);
    }

    private static void OnCompilationStart(CompilationStartAnalysisContext context)
    {
        var compilation = context.Compilation;
        var known = CqrsKnownSymbols.For(compilation);
        if (known.INotification is not { } notification || known.INotificationHandler is not { } handlerInterface ||
            known.NotificationNameAttribute is not { } nameAttribute)
            return;

        var collector = VisibleConfigurationCollector.Start(context);
        if (collector is null) return;

        var gate = new object();
        var handled = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
        var candidates = new List<INamedTypeSymbol>();
        context.RegisterSymbolAction(symbolContext =>
        {
            var type = (INamedTypeSymbol)symbolContext.Symbol;
            if (type.TypeKind != TypeKind.Class || CqrsRegistrationCalls.IsInGeneratedCode(type)) return;

            // A handler the generator subscribes: a concrete, non-generic class it can name.
            if (type is { IsAbstract: false, IsGenericType: false } && IsNameableByGeneratedCode(type))
                foreach (var iface in type.AllInterfaces)
                    if (SymbolEqualityComparer.Default.Equals(iface.OriginalDefinition, handlerInterface))
                        lock (gate) handled.Add(iface.TypeArguments[0]);

            // A notification the module routes as itself, which [NotificationName] could make durable.
            if (type is { IsAbstract: false, IsGenericType: false } && !IsInGenericType(type) &&
                type.Implements(notification) && IsNameableByGeneratedCode(type) && type.FirstSourceLocation() is not null &&
                !HasAttribute(type, nameAttribute))
                lock (gate) candidates.Add(type);
        }, SymbolKind.NamedType);

        context.RegisterCompilationEndAction(endContext =>
        {
            if (collector.Complete() is not { } configuration) return;
            if (!configuration.Calls("UseOutbox") || configuration.SerializerInView || configuration.OutboxOptionsInView) return;

            // The generator's markers for this compilation cover the handlers declared in generated code.
            if (known.CqrsHandledNotificationAttribute is { } marker)
                foreach (var attribute in compilation.Assembly.GetAttributes())
                    if (SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, marker) &&
                        attribute.ConstructorArguments.Length == 1 &&
                        attribute.ConstructorArguments[0] is { Kind: TypedConstantKind.Type, Value: ITypeSymbol type })
                        handled.Add(type);

            foreach (var candidate in candidates)
                if (IsHandled(candidate, handled))
                    endContext.ReportDiagnostic(Diagnostic.Create(
                        CqrsDiagnostics.UnnamedHandledNotification, candidate.FirstSourceLocation(), candidate.Name));
        });
    }

    // A notification reaches every handler of its own type, of a base type or of an interface of it.
    private static bool IsHandled(INamedTypeSymbol notification, HashSet<ITypeSymbol> handled)
    {
        for (ITypeSymbol? current = notification; current is not null; current = current.BaseType)
            if (handled.Contains(current))
                return true;

        foreach (var iface in notification.AllInterfaces)
            if (handled.Contains(iface))
                return true;

        return false;
    }

    // Public or internal all the way out: what generated code in this assembly can name. A file-local type cannot be.
    private static bool IsNameableByGeneratedCode(INamedTypeSymbol type)
    {
        for (var current = type; current is not null; current = current.ContainingType)
            if (current.IsFileLocal ||
                current.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Internal or Accessibility.ProtectedOrInternal))
                return false;
        return true;
    }

    private static bool IsInGenericType(INamedTypeSymbol type)
    {
        for (var current = type.ContainingType; current is not null; current = current.ContainingType)
            if (current.IsGenericType)
                return true;
        return false;
    }

    private static bool HasAttribute(INamedTypeSymbol type, INamedTypeSymbol attribute)
    {
        foreach (var data in type.GetAttributes())
            if (SymbolEqualityComparer.Default.Equals(data.AttributeClass, attribute))
                return true;
        return false;
    }
}
