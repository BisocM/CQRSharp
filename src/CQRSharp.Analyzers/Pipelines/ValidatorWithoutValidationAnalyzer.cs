using System.Collections.Generic;
using System.Collections.Immutable;
using CQRSharp.Shared;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace CQRSharp.Analyzers;

/// <summary>
///     CQRA012: a project registers CQRSharp and declares an <c>IRequestValidator&lt;T&gt;</c>, but none of its registrations
///     adds the validation behavior, so the validator is silently inert. This is security-relevant because unvalidated
///     input then reaches the handler. A call of the builder overload, <c>AddCqrsGenerated(b =&gt; ...)</c>, adds the
///     validation behavior unless its configuration turns it off with <c>UseValidation(false)</c>, and so does the
///     parameterless <c>AddCqrsGenerated()</c>, the builder with nothing configured; the core <c>AddCqrs()</c> adds no
///     behavior at all. It fires only in a compilation
///     that actually registers CQRSharp (a composition root), so a library that declares validators and is wired
///     elsewhere is not flagged.
/// </summary>
/// <remarks>
///     Calls are recognized by the method they bind to, never by name alone: a registration is a method of CQRSharp's
///     <c>DependencyInjectionExtensions</c> or of the generated bootstrap, and the opt-out is <c>ICqrsBuilder.UseValidation</c>
///     with a constant <c>false</c>. A builder configuration that is not a lambda in view, or that turns validation off
///     only through a method of its own, counts as leaving validation on: the analyzer reports only what it can prove.
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ValidatorWithoutValidationAnalyzer : DiagnosticAnalyzer
{
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
        var builderInterface = known.ICqrsBuilder;
        var coreExtensions = known.DependencyInjectionExtensions;
        // Without CQRSharp.Pipelines there is no validation behavior to add, and no builder to add it with.
        if (validatorInterface is null || builderInterface is null || coreExtensions is null) return;

        var validationAdded = new bool[1];
        var registersCqrs = new bool[1];
        var gate = new object();
        var validators = new List<(string validator, string request, Location location)>();

        context.RegisterOperationAction(op =>
        {
            var invocation = (IInvocationOperation)op.Operation;
            var method = invocation.TargetMethod;
            if (!IsRegistration(method.ReducedFrom ?? method, coreExtensions)) return;

            // The builder overload adds it unless its configuration turns it off; the parameterless generated entry point
            // is the builder with nothing configured, so it adds it too. The core AddCqrs() adds no behavior.
            var addsValidation = BuilderConfiguration(invocation, builderInterface) is { } configure
                ? !TurnsValidationOff(configure, builderInterface)
                : (method.ReducedFrom ?? method).Name == "AddCqrsGenerated";
            lock (gate)
            {
                registersCqrs[0] = true;
                if (addsValidation) validationAdded[0] = true;
            }
        }, OperationKind.Invocation);

        context.RegisterSymbolAction(symbolContext =>
        {
            var type = (INamedTypeSymbol)symbolContext.Symbol;
            if (type.TypeKind != TypeKind.Class || type.IsAbstract) return;

            foreach (var iface in type.AllInterfaces)
            {
                if (!iface.IsGenericType) continue;
                if (!SymbolEqualityComparer.Default.Equals(iface.OriginalDefinition, validatorInterface)) continue;

                if (type.FirstSourceLocation() is { } location)
                    lock (gate) validators.Add((type.Name, iface.TypeArguments[0].Name, location));
                break;
            }
        }, SymbolKind.NamedType);

        context.RegisterCompilationEndAction(endContext =>
        {
            if (validationAdded[0] || !registersCqrs[0]) return;

            foreach (var (validator, request, location) in validators)
                endContext.ReportDiagnostic(Diagnostic.Create(
                    CqrsDiagnostics.ValidatorWithoutValidation, location, validator, request));
        });
    }

    // The Action<ICqrsBuilder> a registration is called with; null for a registration without one.
    private static IOperation? BuilderConfiguration(IInvocationOperation invocation, INamedTypeSymbol builderInterface)
    {
        foreach (var argument in invocation.Arguments)
            if (argument.Parameter?.Type is INamedTypeSymbol { IsGenericType: true, TypeArguments.Length: 1 } parameterType &&
                SymbolEqualityComparer.Default.Equals(parameterType.TypeArguments[0], builderInterface))
                return argument.Value;

        return null;
    }

    // Whether the configuration provably turns validation off: a lambda in which every UseValidation call always runs,
    // as a statement of the lambda's body or a link of the fluent chain one is made of, and passes a constant false.
    // A call that may not run (under a condition, in a loop, a nested lambda or a local function) proves nothing, and
    // anything the analyzer cannot see into leaves validation on, the builder's default.
    private static bool TurnsValidationOff(IOperation configure, INamedTypeSymbol builderInterface)
    {
        var value = configure is IDelegateCreationOperation creation ? creation.Target : configure;
        if (value is not IAnonymousFunctionOperation lambda) return false;

        var unconditional = new HashSet<IOperation>();
        foreach (var statement in lambda.Body.Operations)
        {
            var root = statement switch
            {
                IExpressionStatementOperation expression => expression.Operation,
                IReturnOperation { ReturnedValue: { } returned } => returned,
                _ => null
            };
            for (var link = root; link is not null; link = Receiver(link))
                if (IsUseValidation(link, builderInterface))
                    unconditional.Add(link);
        }

        var turnedOff = false;
        foreach (var operation in lambda.Body.Descendants())
        {
            if (!IsUseValidation(operation, builderInterface)) continue;

            var call = (IInvocationOperation)operation;
            if (unconditional.Contains(call) &&
                call.Arguments.Length == 1 && call.Arguments[0].Value.ConstantValue is { HasValue: true, Value: false })
                turnedOff = true;
            else
                return false;
        }

        return turnedOff;
    }

    private static bool IsUseValidation(IOperation operation, INamedTypeSymbol builderInterface)
        => operation is IInvocationOperation { TargetMethod.Name: "UseValidation" } call &&
           SymbolEqualityComparer.Default.Equals(call.TargetMethod.ContainingType, builderInterface);

    // The builder a fluent call is made on: the instance of a call, or the first argument of an extension method's
    // (UseFluentValidation and the store verbs are extensions of the builder).
    private static IOperation? Receiver(IOperation operation)
    {
        while (operation is IConversionOperation conversion) operation = conversion.Operand;
        if (operation is not IInvocationOperation call) return null;

        var receiver = call.Instance ??
                       (call.TargetMethod.IsExtensionMethod && call.Arguments.Length > 0 ? call.Arguments[0].Value : null);
        while (receiver is IConversionOperation inner) receiver = inner.Operand;
        return receiver;
    }

    private static bool IsRegistration(IMethodSymbol definition, INamedTypeSymbol coreExtensions)
    {
        var containing = definition.ContainingType;
        if (SymbolEqualityComparer.Default.Equals(containing, coreExtensions)) return definition.Name == "AddCqrs";

        // The generated entry points, emitted into the consuming assembly itself (namespace CQRSharp, or its module namespace).
        return definition.Name == "AddCqrsGenerated" &&
               containing is { Name: CqrsKnownSymbols.BootstrapTypeName } &&
               containing.ContainingNamespace.ToDisplayString() is var ns &&
               (ns == "CQRSharp" || ns.StartsWith("CQRSharp.Generated.", System.StringComparison.Ordinal));
    }
}
