using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using CQRSharp.Shared;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace CQRSharp.Generators.CqrsAotHintGenerator;

/// <summary>
///     A source generator that creates hints for the Native AOT compiler.
///     It scans the compilation for all request and open generic behavior/handler types
///     and generates explicit `typeof()` references for every possible generic instantiation.
///     This prevents the AOT compiler from trimming code that is only referenced dynamically
///     via dependency injection, which would otherwise cause runtime errors.
/// </summary>
[Generator]
public sealed class CqrsAotHintGenerator : IIncrementalGenerator
{
    private static readonly SymbolDisplayFormat Fq = SymbolDisplayFormat.FullyQualifiedFormat;

    /// <summary>A concrete request, plus every type it and its result are assignable to (for constraint checks).</summary>
    private sealed record AotRequest(
        string RequestName,
        string ResultName,
        EquatableArray<string> RequestAssignableTo,
        EquatableArray<string> ResultAssignableTo,
        AotTypeTraits RequestTraits,
        AotTypeTraits ResultTraits);

    private sealed record AotTypeTraits(bool IsReferenceType, bool IsNonNullableValueType, bool IsUnmanaged, bool HasPublicParameterlessConstructor);

    /// <summary>An open-generic <c>IPipelineBehavior&lt;TRequest, TResult&gt;</c> implementation and its constraints.</summary>
    private sealed record AotBehavior(string Name, AotTypeParameter Request, AotTypeParameter Result);

    private sealed record AotTypeParameter(
        string Name,
        bool RequiresReferenceType,
        bool RequiresValueType,
        bool RequiresUnmanaged,
        bool RequiresConstructor,
        EquatableArray<string> ConstraintTypes);

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // Project concrete requests and open-generic behaviors into small equatable records in the transform, so this
        // generator caches per-changed-file instead of regenerating on every keystroke (and holds no symbols).
        var requests = context.SyntaxProvider
            .CreateSyntaxProvider(
                static (node, _) => node is ClassDeclarationSyntax or RecordDeclarationSyntax,
                static (ctx, ct) => ExtractRequest(ctx, ct))
            .Where(static r => r is not null)
            .Select(static (r, _) => r!)
            .Collect();

        var behaviors = context.SyntaxProvider
            .CreateSyntaxProvider(
                static (node, _) => node is ClassDeclarationSyntax or RecordDeclarationSyntax,
                static (ctx, ct) => ExtractOpenGenericBehavior(ctx, ct))
            .Where(static b => b is not null)
            .Select(static (b, _) => b!)
            .Collect();

        context.RegisterSourceOutput(requests.Combine(behaviors), static (spc, source) =>
        {
            var (requestModels, behaviorModels) = source;
            var sourceCode = GenerateAotHints(requestModels, behaviorModels);
            spc.AddSource("CqrsAotHints.g.cs", SourceText.From(sourceCode, Encoding.UTF8));
        });
    }

    private static AotRequest? ExtractRequest(GeneratorSyntaxContext ctx, CancellationToken ct)
    {
        if (ctx.SemanticModel.GetDeclaredSymbol(ctx.Node, ct) is not INamedTypeSymbol type) return null;
        if (type is not { IsAbstract: false, IsGenericType: false } || !IsAccessibleFromGeneratedCode(type)) return null;
        if (!IsFirstDeclaration(type, ctx.Node)) return null;

        // Every command (ICommand, ICommand<T>) and query reaches the non-streaming pipeline as IRequest<TResponse>,
        // which IQuery<T> directly extends — so the response type argument is the pipeline's TResult for all of them.
        var requestOfT = CqrsKnownSymbols.For(ctx.SemanticModel.Compilation).IQuery?.Interfaces.FirstOrDefault()?.OriginalDefinition;
        if (requestOfT is null) return null;

        var requestInterface = type.AllInterfaces.FirstOrDefault(i => SymbolEqualityComparer.Default.Equals(i.OriginalDefinition, requestOfT));
        if (requestInterface is null) return null;

        var result = requestInterface.TypeArguments[0];
        if (!IsNameable(result)) return null;

        return new AotRequest(
            type.ToDisplayString(Fq),
            result.ToDisplayString(Fq),
            AssignableTo(type),
            AssignableTo(result),
            TraitsOf(type),
            TraitsOf(result));
    }

    private static AotBehavior? ExtractOpenGenericBehavior(GeneratorSyntaxContext ctx, CancellationToken ct)
    {
        if (ctx.SemanticModel.GetDeclaredSymbol(ctx.Node, ct) is not INamedTypeSymbol type) return null;
        if (type is not { IsAbstract: false, IsGenericType: true } || !IsAccessibleFromGeneratedCode(type)) return null;
        if (!IsFirstDeclaration(type, ctx.Node)) return null;

        // Only the shape DI can close over a request: exactly <TRequest, TResult>, not nested in another generic.
        if (type.TypeParameters.Length != 2 || type.ContainingType is { IsGenericType: true }) return null;

        var pipelineBehaviorSymbol = CqrsKnownSymbols.For(ctx.SemanticModel.Compilation).IPipelineBehavior;
        if (pipelineBehaviorSymbol is null) return null;

        // ...implementing IPipelineBehavior<TRequest, TResult> over its own two parameters, in that order.
        var implemented = type.AllInterfaces.FirstOrDefault(i => SymbolEqualityComparer.Default.Equals(i.OriginalDefinition, pipelineBehaviorSymbol));
        if (implemented is null ||
            !SymbolEqualityComparer.Default.Equals(implemented.TypeArguments[0], type.TypeParameters[0]) ||
            !SymbolEqualityComparer.Default.Equals(implemented.TypeArguments[1], type.TypeParameters[1]))
            return null;

        return new AotBehavior(
            type.ToDisplayString(Fq).Split('<')[0],
            ParameterOf(type.TypeParameters[0]),
            ParameterOf(type.TypeParameters[1]));
    }

    private static AotTypeParameter ParameterOf(ITypeParameterSymbol parameter) =>
        new(
            parameter.Name,
            parameter.HasReferenceTypeConstraint,
            parameter.HasValueTypeConstraint,
            parameter.HasUnmanagedTypeConstraint,
            parameter.HasConstructorConstraint,
            new EquatableArray<string>(parameter.ConstraintTypes.Select(c => c.ToDisplayString(Fq)).ToArray()));

    // Self, base classes and all interfaces, fully qualified: what a "where T : X" constraint can be satisfied by.
    private static EquatableArray<string> AssignableTo(ITypeSymbol type)
    {
        var names = new List<string> { type.ToDisplayString(Fq) };
        for (var current = type.BaseType; current is not null; current = current.BaseType)
            names.Add(current.ToDisplayString(Fq));
        names.AddRange(type.AllInterfaces.Select(i => i.ToDisplayString(Fq)));
        return new EquatableArray<string>(names.Distinct(StringComparer.Ordinal).ToArray());
    }

    private static AotTypeTraits TraitsOf(ITypeSymbol type) =>
        new(
            type.IsReferenceType,
            type.IsValueType && type.OriginalDefinition.SpecialType != SpecialType.System_Nullable_T,
            type.IsUnmanagedType,
            type.IsValueType ||
            (type is INamedTypeSymbol { IsAbstract: false } named &&
             named.InstanceConstructors.Any(c => c.Parameters.Length == 0 && c.DeclaredAccessibility == Accessibility.Public)));

    private static bool IsFirstDeclaration(INamedTypeSymbol type, SyntaxNode node)
    {
        var declarations = type.DeclaringSyntaxReferences;
        return declarations.Length <= 1 ||
               (declarations[0].SyntaxTree == node.SyntaxTree && declarations[0].Span == node.Span);
    }

    private static bool IsNameable(ITypeSymbol type) => type switch
    {
        IArrayTypeSymbol array => IsNameable(array.ElementType),
        INamedTypeSymbol named => IsAccessibleFromGeneratedCode(named) && named.TypeArguments.All(IsNameable),
        _ => false
    };

    private static bool IsAccessibleFromGeneratedCode(INamedTypeSymbol typeSymbol)
    {
        for (var current = typeSymbol; current is not null; current = current.ContainingType)
            if (current.IsFileLocal || current.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Internal))
                return false;
        return true;
    }

    /// <summary>
    ///     Whether closing <paramref name="behavior" /> over <paramref name="request" /> compiles. A mismatch is not an
    ///     error — DI never builds that closed type either (it fails the same constraints at runtime) — so the pair is
    ///     simply skipped. Anything that cannot be positively verified from the projected names (e.g. a constraint met
    ///     only through variance) is skipped too: an absent hint costs nothing for a pair that was never instantiated,
    ///     whereas a wrong one is a compile error in the consumer's project.
    /// </summary>
    private static bool Satisfies(AotBehavior behavior, AotRequest request)
    {
        return Check(behavior.Request, request.RequestAssignableTo, request.RequestTraits) &&
               Check(behavior.Result, request.ResultAssignableTo, request.ResultTraits);

        bool Check(AotTypeParameter parameter, EquatableArray<string> assignableTo, AotTypeTraits traits)
        {
            if (parameter.RequiresReferenceType && !traits.IsReferenceType) return false;
            if (parameter.RequiresValueType && !traits.IsNonNullableValueType) return false;
            if (parameter.RequiresUnmanaged && !traits.IsUnmanaged) return false;
            if (parameter.RequiresConstructor && !traits.HasPublicParameterlessConstructor) return false;

            foreach (var constraint in parameter.ConstraintTypes)
            {
                // A constraint may mention the behavior's own parameters ("where TRequest : IRequest<TResult>"). In the
                // fully-qualified format every real type is "global::"-prefixed or a keyword, so a bare identifier
                // equal to a parameter name can only be that type parameter.
                var closed = Substitute(Substitute(constraint, behavior.Request.Name, request.RequestName), behavior.Result.Name, request.ResultName);
                if (!assignableTo.Contains(closed, StringComparer.Ordinal)) return false;
            }

            return true;
        }
    }

    private static string Substitute(string constraint, string parameterName, string typeName)
        => Regex.Replace(constraint, $@"(?<![\w:.]){Regex.Escape(parameterName)}(?!\w)", typeName.Replace("$", "$$"));

    private static string GenerateAotHints(ImmutableArray<AotRequest> requests, ImmutableArray<AotBehavior> openGenericBehaviors)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("// This file is generated to provide hints to the AOT compiler. Do not edit manually.");
        sb.AppendLine();
        sb.AppendLine("using System.Runtime.CompilerServices;");
        sb.AppendLine();
        sb.AppendLine("namespace CQRSharp.Core.AotHints");
        sb.AppendLine("{");
        sb.AppendLine("    internal static class AotHintProvider");
        sb.AppendLine("    {");
        sb.AppendLine("        // Module initializer ensures these references are rooted and survive trimming.");
        sb.AppendLine("        [ModuleInitializer]");
        sb.AppendLine("        internal static void Initialize()");
        sb.AppendLine("        {");

        sb.AppendLine("            // Preserving Pipeline Behavior instantiations:");
        foreach (var request in requests)
        foreach (var behavior in openGenericBehaviors)
            if (Satisfies(behavior, request))
                sb.AppendLine($"            _ = typeof({behavior.Name}<{request.RequestName}, {request.ResultName}>);");

        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine("}");

        return sb.ToString();
    }
}
