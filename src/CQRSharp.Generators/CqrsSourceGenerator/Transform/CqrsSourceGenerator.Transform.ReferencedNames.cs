using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using CQRSharp.Generators;
using CQRSharp.Shared;
using Microsoft.CodeAnalysis;

namespace CQRSharp.Generators.CqrsSourceGenerator;

public sealed partial class CqrsSourceGenerator
{
    // A module assembly's names depend on that assembly alone, so they are read once per assembly symbol, which Roslyn
    // shares between the compilations of an unchanged reference: an edit does not walk the references again.
    private static readonly ConditionalWeakTable<IAssemblySymbol, ModuleNames> ReferencedModuleNamesCache = new();

    /// <summary>
    ///     The stable names every referenced module gives (see <see cref="ReferencedNameModel" />), read from its metadata
    ///     the way its own generator read them from source: the <c>[NotificationName]</c>s of the notifications it declares
    ///     and can name, and the names of the notification handlers it subscribes. What a module's generator would have
    ///     rejected (a duplicate within it, a notification it cannot serialize) is an error there, so it is not second-guessed
    ///     here; a handler this reading cannot prove subscribed is left out, so a clash is never reported that is not one.
    /// </summary>
    private static (ReferencedNameModel[] Notifications, ReferencedNameModel[] Handlers) GetReferencedModuleNames(Compilation compilation, CqrsKnownSymbols known)
    {
        var moduleMarker = known.CqrsGeneratedModuleAttribute;
        if (moduleMarker is null) return (Array.Empty<ReferencedNameModel>(), Array.Empty<ReferencedNameModel>());

        var notifications = new List<ReferencedNameModel>();
        var handlers = new List<ReferencedNameModel>();
        foreach (var reference in compilation.SourceModule.ReferencedAssemblySymbols)
        {
            var registrar = reference.GetAttributes()
                .Where(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, moduleMarker))
                .Select(a => a.ConstructorArguments.FirstOrDefault().Value as INamedTypeSymbol)
                .FirstOrDefault(r => r is not null);
            if (registrar is null) continue;

            var names = ReferencedModuleNamesCache.GetValue(reference, static assembly => ReadModuleNames(assembly));
            var registrarName = registrar.ToDisplayString(Fq);
            notifications.AddRange(names.Notifications.Select(n => new ReferencedNameModel(n.Name, n.TypeName, reference.Name, registrarName)));
            handlers.AddRange(names.Handlers.Select(h => new ReferencedNameModel(h.Name, h.TypeName, reference.Name, registrarName)));
        }

        return (Ordered(notifications), Ordered(handlers));

        static ReferencedNameModel[] Ordered(List<ReferencedNameModel> names)
            => names
                .Distinct()
                .OrderBy(n => n.Name, StringComparer.Ordinal)
                .ThenBy(n => n.AssemblyName, StringComparer.Ordinal)
                .ThenBy(n => n.TypeName, StringComparer.Ordinal)
                .ToArray();
    }

    // Recognized by metadata name, not by the symbols of a compilation, so what is cached holds for every compilation
    // that shares the assembly symbol.
    private static ModuleNames ReadModuleNames(IAssemblySymbol assembly)
    {
        var notifications = new List<(string Name, string TypeName)>();
        var handlers = new List<(string Name, string TypeName)>();
        Visit(assembly.GlobalNamespace);
        return new ModuleNames(notifications, handlers);

        void Visit(INamespaceSymbol ns)
        {
            foreach (var child in ns.GetNamespaceMembers())
                Visit(child);
            foreach (var type in ns.GetTypeMembers())
                VisitType(type);
        }

        void VisitType(INamedTypeSymbol type)
        {
            // Generated code registers only concrete, non-generic types it can name; nested types count unless they sit
            // in a generic type, whose constructions are not declared anywhere.
            if (type.IsGenericType) return;
            foreach (var nested in type.GetTypeMembers())
                VisitType(nested);

            if (type.IsAbstract || !IsNameableWithin(type, assembly)) return;

            if (type.TypeKind == TypeKind.Class && StableNameOf(type) is { } stableName && ImplementsNotification(type))
                notifications.Add((stableName, type.ToDisplayString(Fq)));

            if (type.TypeKind == TypeKind.Class &&
                type.AllInterfaces.Any(i => IsFrameworkType(i.OriginalDefinition, "INotificationHandler", 1) && IsNameableWithin(i.TypeArguments[0], assembly)))
                handlers.Add((HandlerNameOf(type), type.ToDisplayString(Fq)));
        }
    }

    // What generated code in the assembly can name: public or internal at every level of nesting (a type of another
    // assembly public throughout, since this reading does not follow InternalsVisibleTo), with a name code can spell, not
    // marked [Obsolete] as an error, and the same for every type argument.
    private static bool IsNameableWithin(ITypeSymbol type, IAssemblySymbol assembly)
    {
        switch (type)
        {
            case IArrayTypeSymbol array:
                return IsNameableWithin(array.ElementType, assembly);
            case INamedTypeSymbol named:
                var sameAssembly = SymbolEqualityComparer.Default.Equals(named.ContainingAssembly, assembly);
                for (var current = named; current is not null; current = current.ContainingType)
                {
                    if (!current.CanBeReferencedByName || GeneratedCodeSuppressions.IsObsoleteAsError(current)) return false;
                    var accessibility = current.DeclaredAccessibility;
                    if (accessibility != Accessibility.Public &&
                        !(sameAssembly && accessibility is Accessibility.Internal or Accessibility.ProtectedOrInternal))
                        return false;
                }

                return named.TypeArguments.All(argument => IsNameableWithin(argument, assembly));
            default:
                return false;
        }
    }

    private static bool ImplementsNotification(INamedTypeSymbol type)
        => type.AllInterfaces.Any(i => IsFrameworkType(i, "INotification", 0));

    // The [NotificationName] value, as BuildNotification reads it: absent or blank is no name.
    private static string? StableNameOf(INamedTypeSymbol type)
    {
        foreach (var attribute in type.GetAttributes())
            if (attribute.AttributeClass is { } attributeClass && IsFrameworkType(attributeClass, "NotificationNameAttribute", 0) &&
                attribute.ConstructorArguments.FirstOrDefault().Value is string name && !string.IsNullOrWhiteSpace(name))
                return name;
        return null;
    }

    // The [NotificationHandlerName] value, or the type's namespace-qualified name, as BuildNotificationHandlerName reads it.
    private static string HandlerNameOf(INamedTypeSymbol type)
    {
        foreach (var attribute in type.GetAttributes())
            if (attribute.AttributeClass is { } attributeClass && IsFrameworkType(attributeClass, "NotificationHandlerNameAttribute", 0) &&
                attribute.ConstructorArguments.FirstOrDefault().Value is string name && !string.IsNullOrWhiteSpace(name))
                return name;
        return DefaultNotificationHandlerName(type);
    }

    private static bool IsFrameworkType(INamedTypeSymbol type, string name, int arity)
        => type.Name == name && type.Arity == arity &&
           type.ContainingNamespace is { Name: "CQRSharp", ContainingNamespace.IsGlobalNamespace: true } &&
           type.ContainingAssembly?.Name == "CQRSharp.Abstractions";

    private sealed record ModuleNames(List<(string Name, string TypeName)> Notifications, List<(string Name, string TypeName)> Handlers);
}
