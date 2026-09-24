using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using CQRSharp.Shared;
using Microsoft.CodeAnalysis;

namespace CQRSharp.Generators.CqrsSourceGenerator;

public sealed partial class CqrsSourceGenerator
{
    /// <summary>What a behavior wraps: a request's pipeline, a stream request's, or a notification's delivery.</summary>
    private enum BehaviorKind
    {
        Pipeline,
        Stream,
        Notification
    }

    /// <summary>
    ///     An open-generic behavior in view of this compilation: a pipeline behavior <c>B&lt;TRequest, TResult&gt;</c>, a
    ///     stream behavior <c>B&lt;TRequest, TItem&gt;</c> or a notification behavior <c>B&lt;TNotification&gt;</c>.
    ///     <c>Blocked</c> says why generated code cannot close it, when it cannot; <c>SuppressedWarnings</c> are the
    ///     diagnostics naming it raises, which the closing code suppresses.
    /// </summary>
    private sealed class OpenBehavior(INamedTypeSymbol type, BehaviorKind kind, string? blocked, string[] suppressedWarnings)
    {
        public INamedTypeSymbol Type { get; } = type;
        public BehaviorKind Kind { get; } = kind;
        public string? Blocked { get; } = blocked;
        public EquatableArray<string> SuppressedWarnings { get; } = new(suppressedWarnings);
    }

    private static readonly ConditionalWeakTable<Compilation, IReadOnlyList<OpenBehavior>> OpenBehaviorCache = new();

    /// <summary>
    ///     Every open-generic <c>IPipelineBehavior&lt;TRequest, TResult&gt;</c>, <c>IStreamPipelineBehavior&lt;TRequest, TItem&gt;</c>
    ///     and <c>INotificationPipelineBehavior&lt;TNotification&gt;</c> implementation in view: the ones declared in this
    ///     compilation, CQRSharp's own, and those of every referenced assembly that references the behavior interfaces,
    ///     including the ones generated code cannot close. Scanned once per compilation; assemblies that cannot contain a
    ///     behavior are not walked.
    /// </summary>
    private static IReadOnlyList<OpenBehavior> OpenBehaviorsOf(Compilation compilation)
        => OpenBehaviorCache.GetValue(compilation, static c => CollectOpenBehaviors(c, CqrsKnownSymbols.For(c)));

    private static IReadOnlyList<OpenBehavior> CollectOpenBehaviors(Compilation compilation, CqrsKnownSymbols known)
    {
        var pipeline = known.IPipelineBehavior;
        var stream = known.IStreamPipelineBehavior;
        var notification = known.INotificationPipelineBehavior;
        if (pipeline is null && stream is null && notification is null) return [];

        var found = new List<OpenBehavior>();
        // The assembly that defines the behavior interfaces (CQRSharp.Core): only an assembly that references it
        // can declare a behavior. Its manifest names it whenever it implements one, whatever else the project
        // references.
        var behaviorAssembly = (pipeline ?? stream ?? notification)!.ContainingAssembly?.Name;
        Visit(compilation.Assembly.GlobalNamespace);
        foreach (var reference in compilation.References)
        {
            if (compilation.GetAssemblyOrModuleSymbol(reference) is not IAssemblySymbol assembly) continue;
            if (!References(assembly, behaviorAssembly)) continue;
            Visit(assembly.GlobalNamespace);
        }

        return found;

        // Namespaces and types only: GetMembers() would also build a symbol for every method, property and field of
        // every source type on each run, which is most of the cost in a large project.
        void Visit(INamespaceSymbol ns)
        {
            foreach (var child in ns.GetNamespaceMembers())
                Visit(child);
            foreach (var type in ns.GetTypeMembers())
                VisitType(type);
        }

        void VisitType(INamedTypeSymbol type)
        {
            // A type may implement both request behavior interfaces; each is its own behavior.
            var isPipeline = IsOpenBehaviorShape(type, pipeline);
            var isStream = IsOpenBehaviorShape(type, stream);
            var isNotification = IsOpenBehaviorShape(type, notification);
            if (isPipeline || isStream || isNotification)
            {
                var blocked = WhyNotClosable(type, compilation, known);
                var suppressed = blocked is null ? SuppressedWarningsOf(type, known) : [];
                if (isPipeline) found.Add(new OpenBehavior(type, BehaviorKind.Pipeline, blocked, suppressed));
                if (isStream) found.Add(new OpenBehavior(type, BehaviorKind.Stream, blocked, suppressed));
                if (isNotification) found.Add(new OpenBehavior(type, BehaviorKind.Notification, blocked, suppressed));
            }

            foreach (var nested in type.GetTypeMembers())
                VisitType(nested);
        }
    }

    private static readonly ConditionalWeakTable<Compilation, IReadOnlyList<INamedTypeSymbol>> ReferencedValueTypeNotificationCache = new();

    /// <summary>
    ///     The value-type notifications declared in the referenced assemblies (a library's, a contracts project's) that a
    ///     behavior of this compilation may have to be closed over: every non-generic struct implementing
    ///     <c>INotification</c>, whether or not generated code here can name it, plus the constructed generic ones a
    ///     referenced module handles (its <c>[CqrsHandledNotification]</c> markers), which no declaration lists. Scanned
    ///     once per compilation; only assemblies that reference <c>INotification</c>'s assembly are walked.
    /// </summary>
    private static IReadOnlyList<INamedTypeSymbol> ReferencedValueTypeNotificationsOf(Compilation compilation)
        => ReferencedValueTypeNotificationCache.GetValue(compilation, static c => CollectReferencedValueTypeNotifications(c, CqrsKnownSymbols.For(c)));

    private static IReadOnlyList<INamedTypeSymbol> CollectReferencedValueTypeNotifications(Compilation compilation, CqrsKnownSymbols known)
    {
        var notification = known.INotification;
        if (notification is null) return [];

        var notificationAssembly = notification.ContainingAssembly?.Name;
        var found = new List<INamedTypeSymbol>();
        var seen = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        foreach (var assembly in compilation.SourceModule.ReferencedAssemblySymbols)
        {
            if (!References(assembly, notificationAssembly)) continue;
            Visit(assembly.GlobalNamespace);

            if (known.CqrsHandledNotificationAttribute is { } marker)
                foreach (var attribute in assembly.GetAttributes())
                    if (SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, marker) &&
                        attribute.ConstructorArguments.Length == 1 &&
                        attribute.ConstructorArguments[0].Value is INamedTypeSymbol { IsValueType: true, IsGenericType: true } handled &&
                        !CqrsKnownSymbols.ContainsTypeParameter(handled) &&
                        seen.Add(handled))
                        found.Add(handled);
        }

        return found;

        void Visit(INamespaceSymbol ns)
        {
            foreach (var child in ns.GetNamespaceMembers())
                Visit(child);
            foreach (var type in ns.GetTypeMembers())
                VisitType(type);
        }

        // Nested types count, except inside a generic type: their constructions are not declared anywhere.
        void VisitType(INamedTypeSymbol type)
        {
            if (type.IsGenericType) return;
            if (type is { TypeKind: TypeKind.Struct, IsRefLikeType: false } &&
                type.AllInterfaces.Contains(notification, SymbolEqualityComparer.Default) &&
                seen.Add(type))
                found.Add(type);

            foreach (var nested in type.GetTypeMembers())
                VisitType(nested);
        }
    }

    private static bool References(IAssemblySymbol assembly, string? target)
    {
        if (target is null) return false;
        if (assembly.Name == target) return true;
        foreach (var module in assembly.Modules)
        foreach (var referenced in module.ReferencedAssemblies)
            if (referenced.Name == target)
                return true;

        return false;
    }

    // A non-abstract class generic over exactly the behavior interface's type parameters (B<T0, T1> for a request or
    // stream behavior, B<T> for a notification behavior) that implements the interface over them, in order.
    private static bool IsOpenBehaviorShape(INamedTypeSymbol type, INamedTypeSymbol? behaviorInterface)
    {
        if (behaviorInterface is null) return false;
        var arity = behaviorInterface.TypeParameters.Length;
        if (type is not { IsGenericType: true, IsAbstract: false, IsStatic: false, TypeKind: TypeKind.Class } || type.TypeParameters.Length != arity) return false;

        foreach (var iface in type.AllInterfaces)
        {
            if (!iface.IsGenericType || !SymbolEqualityComparer.Default.Equals(iface.OriginalDefinition, behaviorInterface)) continue;
            if (iface.TypeArguments.Length != arity) continue;

            var matches = true;
            for (var i = 0; i < arity; i++)
                matches &= SymbolEqualityComparer.Default.Equals(iface.TypeArguments[i], type.TypeParameters[i]);
            if (matches) return true;
        }

        return false;
    }

    // Why generated code in this compilation cannot write B<Request, Result> (or B<Notification>), or null when it can. The reason completes
    // the sentence "the behavior cannot be closed because ...".
    private static string? WhyNotClosable(INamedTypeSymbol type, Compilation compilation, CqrsKnownSymbols known)
    {
        // Closed over a request or a notification, a behavior nested in a generic type would need its container's type
        // arguments too.
        for (var container = type.ContainingType; container is not null; container = container.ContainingType)
            if (container.TypeParameters.Length > 0)
                return "it is nested in a generic type";

        for (var current = type; current is not null; current = current.ContainingType)
            if (current.IsFileLocal)
                return "it is file-local";

        // [Obsolete(error: true)] is an error no pragma can suppress (CS0619); asked before accessibility, which
        // refuses such a type too but would name the wrong reason.
        if (GeneratedCodeSuppressions.IsObsoleteAsError(type))
            return "it is marked [Obsolete] as an error";

        if (!GeneratedCodeAccessibility.IsAccessible(type, compilation))
            return SymbolEqualityComparer.Default.Equals(type.ContainingAssembly, compilation.Assembly)
                ? "it is less accessible than internal"
                : $"it is internal to '{type.ContainingAssembly.Name}', which does not grant '{compilation.AssemblyName}' access (InternalsVisibleTo)";

        return null;
    }

    // Naming an [Obsolete] or [Experimental] behavior is a warning (CS0612/CS0618, or the attribute's DiagnosticId) or a
    // suppressible error (the experimental diagnostic id); the generated closing code suppresses exactly those around
    // its entry, so a warnaserror build keeps compiling while the behavior still runs.
    private static string[] SuppressedWarningsOf(INamedTypeSymbol type, CqrsKnownSymbols known)
    {
        var ids = new SortedSet<string>(StringComparer.Ordinal);
        if (IsDeprecated(type, known)) ids.UnionWith(GeneratedCodeSuppressions.CompilerObsoleteIds);
        GeneratedCodeSuppressions.CollectType(type, ids);
        return ids.ToArray();
    }

    private static bool IsDeprecated(INamedTypeSymbol type, CqrsKnownSymbols known)
    {
        if (known.ObsoleteAttribute is not { } obsolete) return false;
        for (var current = type; current is not null; current = current.ContainingType)
            if (current.GetAttributes().Any(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, obsolete)))
                return true;
        return false;
    }
}
