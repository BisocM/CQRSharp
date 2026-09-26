using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using CQRSharp.Shared;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace CQRSharp.Analyzers;

/// <summary>
///     The CQRSharp configuration of an application whose every part is in view: the one registration, and the builder verbs
///     it is configured with. The analyzers that report a verb as missing (CQRA018, CQRA019, CQRA020) report only against
///     one of these, which <see cref="VisibleConfigurationCollector" /> builds only when nothing else can configure CQRSharp
///     for the application.
/// </summary>
internal sealed class VisibleConfiguration
{
    private readonly ImmutableHashSet<string> _verbs;

    public VisibleConfiguration(IInvocationOperation registration, ImmutableHashSet<string> verbs, bool serializerInView, bool outboxOptionsInView)
    {
        Registration = registration;
        _verbs = verbs;
        SerializerInView = serializerInView;
        OutboxOptionsInView = outboxOptionsInView;
    }

    /// <summary>The application's one <c>AddCqrsGenerated</c> call.</summary>
    public IInvocationOperation Registration { get; }

    /// <summary>
    ///     Whether code in view may choose the notification serializer (<c>AddNotificationSerializer</c>, a type
    ///     implementing or naming <c>INotificationSerializer</c>), so the generated one may not be the one in use.
    /// </summary>
    public bool SerializerInView { get; }

    /// <summary>Whether code in view may set the outbox options, and so the outbox mode, outside the builder.</summary>
    public bool OutboxOptionsInView { get; }

    /// <summary>Whether the configuration calls the <see cref="ICqrsBuilder" /> verb named <paramref name="verb" />.</summary>
    public bool Calls(string verb) => _verbs.Contains(verb);
}

/// <summary>
///     Proves, or fails to prove, that an application's CQRSharp configuration is all in view, and hands it over as a
///     <see cref="VisibleConfiguration" /> when it is. Nothing is reported unless every one of these holds:
///     <list type="bullet">
///         <item>The compilation is an application (an executable), not a library another assembly may configure further.</item>
///         <item>
///             No referenced assembly other than CQRSharp's own can configure CQRSharp: none references CQRSharp.Core or
///             CQRSharp.Pipelines (a module, or a library that registers or configures behaviors), none references
///             CQRSharp.Abstractions together with the dependency-injection abstractions (it could register a notification
///             serializer), and no assembly-scanning registration library is referenced.
///         </item>
///         <item>
///             The compilation calls one CQRSharp registration, once, and it is <c>AddCqrsGenerated()</c> or
///             <c>AddCqrsGenerated(b =&gt; ...)</c> with a lambda that is a chain of builder verbs on its parameter, with
///             arguments that cannot reach the service collection or code of the compilation's own; no method group of a
///             registration is taken.
///         </item>
///         <item>
///             No code in view names a built-in behavior a verb registers, or the open behavior interfaces other than in a
///             plain registration of the application's own behavior (an assembly scan could register the built-in ones).
///         </item>
///     </list>
/// </summary>
/// <remarks>
///     Code the CQRSharp source generator emits is left out: it names the behaviors (the closed behavior factories) and
///     calls the builder on the application's behalf. Every other generated file is analyzed, since another generator could
///     register services too.
/// </remarks>
internal sealed class VisibleConfigurationCollector
{
    // The packages whose code is known: none of them registers a pipeline behavior or a notification serializer.
    private static readonly ImmutableHashSet<string> CqrsSharpAssemblies = ImmutableHashSet.Create(
        StringComparer.Ordinal,
        "CQRSharp.Abstractions", "CQRSharp.Core", "CQRSharp.Pipelines", "CQRSharp.Redis", "CQRSharp.EntityFrameworkCore",
        "CQRSharp.FluentValidation", "CQRSharp.AspNetCore", "CQRSharp.Testing", "CQRSharp.Testing.Xunit.V3");

    // Builder extensions of CQRSharp's own packages that the chain may use; neither registers a verb's behavior.
    private static readonly ImmutableHashSet<string> KnownBuilderExtensionAssemblies = ImmutableHashSet.Create(
        StringComparer.Ordinal, "CQRSharp.FluentValidation", "CQRSharp.EntityFrameworkCore");

    // Libraries that register services by scanning assemblies, which could pick up CQRSharp's built-in behaviors.
    private static readonly ImmutableHashSet<string> ScanningLibraries = ImmutableHashSet.Create(StringComparer.Ordinal, "Scrutor");

    private readonly CqrsKnownSymbols _known;
    private readonly INamedTypeSymbol _builder;
    private readonly INamedTypeSymbol _coreExtensions;
    private readonly INamedTypeSymbol? _builderBootstrap;
    private readonly INamedTypeSymbol? _serviceCollection;
    private readonly INamedTypeSymbol? _serviceProvider;
    private readonly INamedTypeSymbol? _serializer;
    private readonly INamedTypeSymbol? _outboxOptions;
    private readonly INamedTypeSymbol? _func2;
    private readonly ImmutableArray<INamedTypeSymbol> _builtInBehaviors;

    private readonly object _gate = new();
    private readonly List<IInvocationOperation> _registrations = new();
    private bool _opaque;
    private bool _serializerInView;
    private bool _outboxOptionsInView;

    private VisibleConfigurationCollector(Compilation compilation, CqrsKnownSymbols known, INamedTypeSymbol builder, INamedTypeSymbol coreExtensions)
    {
        _known = known;
        _builder = builder;
        _coreExtensions = coreExtensions;
        _builderBootstrap = compilation.GetTypeByMetadataName("CQRSharp.Pipelines.CqrsBuilderBootstrap");
        _serviceCollection = compilation.GetTypeByMetadataName("Microsoft.Extensions.DependencyInjection.IServiceCollection");
        _serviceProvider = compilation.GetTypeByMetadataName("System.IServiceProvider");
        _serializer = compilation.GetTypeByMetadataName("CQRSharp.Persistence.INotificationSerializer");
        _outboxOptions = compilation.GetTypeByMetadataName("CQRSharp.OutboxOptions");
        _func2 = compilation.GetTypeByMetadataName("System.Func`2");
        _builtInBehaviors = new[]
            {
                "CQRSharp.Pipelines.IdempotencyBehavior`2", "CQRSharp.Pipelines.StreamIdempotencyBehavior`2",
                "CQRSharp.Pipelines.ResilienceBehavior`2", "CQRSharp.Pipelines.StreamResilienceBehavior`2"
            }
            .Select(compilation.GetTypeByMetadataName)
            .Where(t => t is not null)
            .Select(t => t!)
            .ToImmutableArray();
    }

    /// <summary>
    ///     Starts collecting for the compilation, or returns <see langword="null" /> (registering nothing) when it can
    ///     already tell that the configuration is not all in view.
    /// </summary>
    public static VisibleConfigurationCollector? Start(CompilationStartAnalysisContext context)
    {
        var compilation = context.Compilation;
        if (compilation.Options.OutputKind is not (OutputKind.ConsoleApplication or OutputKind.WindowsApplication or OutputKind.WindowsRuntimeApplication))
            return null;

        var known = CqrsKnownSymbols.For(compilation);
        if (known.ICqrsBuilder is not { } builder || known.DependencyInjectionExtensions is not { } coreExtensions) return null;
        if (ReferencesAssemblyThatCanConfigure(compilation)) return null;

        var collector = new VisibleConfigurationCollector(compilation, known, builder, coreExtensions);
        context.RegisterOperationAction(collector.OnInvocation, OperationKind.Invocation);
        context.RegisterOperationAction(collector.OnMethodReference, OperationKind.MethodReference);
        context.RegisterOperationAction(collector.OnTypeOf, OperationKind.TypeOf);
        context.RegisterOperationAction(collector.OnObjectCreation, OperationKind.ObjectCreation);
        context.RegisterSymbolAction(collector.OnNamedType, SymbolKind.NamedType);
        return collector;
    }

    /// <summary>The configuration, once every operation was seen; <see langword="null" /> when it is not all in view.</summary>
    public VisibleConfiguration? Complete()
    {
        lock (_gate)
        {
            if (_opaque || _registrations.Count != 1) return null;

            var registration = _registrations[0];
            var method = registration.TargetMethod.ReducedFrom ?? registration.TargetMethod;
            if (method.Name != "AddCqrsGenerated") return null;

            var verbs = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
            if (CqrsRegistrationCalls.BuilderConfiguration(registration, _builder) is { } configure && !TryReadVerbs(configure, verbs))
                return null;

            return new VisibleConfiguration(registration, verbs.ToImmutable(), _serializerInView, _outboxOptionsInView);
        }
    }

    // Whether a referenced assembly other than CQRSharp's own could add to the configuration: by configuring the builder or
    // registering a behavior (it references CQRSharp.Core or CQRSharp.Pipelines), by registering a notification serializer
    // (CQRSharp.Abstractions with the dependency-injection abstractions), or by scanning assemblies for services.
    private static bool ReferencesAssemblyThatCanConfigure(Compilation compilation)
    {
        foreach (var assembly in compilation.SourceModule.ReferencedAssemblySymbols)
        {
            var name = assembly.Identity.Name;
            if (ScanningLibraries.Contains(name)) return true;
            if (CqrsSharpAssemblies.Contains(name)) continue;

            var abstractions = false;
            var dependencyInjection = false;
            foreach (var module in assembly.Modules)
            foreach (var referenced in module.ReferencedAssemblies)
            {
                switch (referenced.Name)
                {
                    case "CQRSharp.Core" or "CQRSharp.Pipelines":
                        return true;
                    case "CQRSharp.Abstractions":
                        abstractions = true;
                        break;
                    case "Microsoft.Extensions.DependencyInjection.Abstractions":
                        dependencyInjection = true;
                        break;
                }

                if (abstractions && dependencyInjection) return true;
            }
        }

        return false;
    }

    private void OnInvocation(OperationAnalysisContext context)
    {
        if (CqrsRegistrationCalls.IsInGeneratedCode(context.ContainingSymbol)) return;

        var invocation = (IInvocationOperation)context.Operation;
        var method = invocation.TargetMethod;
        var definition = method.ReducedFrom ?? method;

        lock (_gate)
        {
            if (CqrsRegistrationCalls.IsRegistration(definition, _coreExtensions) ||
                SymbolEqualityComparer.Default.Equals(definition.ContainingType, _builderBootstrap))
                _registrations.Add(invocation);

            if (definition.Name == "AddNotificationSerializer" && SymbolEqualityComparer.Default.Equals(definition.ContainingType, _coreExtensions))
                _serializerInView = true;

            foreach (var typeArgument in method.TypeArguments)
                NoteMentions(typeArgument);
        }
    }

    // A registration handed on as a method group runs wherever the delegate is invoked, with whatever it is given.
    private void OnMethodReference(OperationAnalysisContext context)
    {
        if (CqrsRegistrationCalls.IsInGeneratedCode(context.ContainingSymbol)) return;

        var method = ((IMethodReferenceOperation)context.Operation).Method;
        var definition = method.ReducedFrom ?? method;
        if (CqrsRegistrationCalls.IsRegistration(definition, _coreExtensions) ||
            SymbolEqualityComparer.Default.Equals(definition.ContainingType, _builderBootstrap))
            lock (_gate) _opaque = true;
    }

    private void OnTypeOf(OperationAnalysisContext context)
    {
        if (CqrsRegistrationCalls.IsInGeneratedCode(context.ContainingSymbol)) return;

        // An attribute argument (a [PipelineExemption], say) names a type; it registers nothing.
        var typeOf = (ITypeOfOperation)context.Operation;
        if (typeOf.Syntax.FirstAncestorOrSelf<AttributeSyntax>() is not null) return;

        lock (_gate)
        {
            NoteMentions(typeOf.TypeOperand);

            // typeof(IPipelineBehavior<,>) registers the application's own open behavior, or scans for every one, the
            // built-in ones included. Only the first, in a plain container registration, is in view.
            if (typeOf.TypeOperand is INamedTypeSymbol { IsUnboundGenericType: true } unbound &&
                (IsDefinition(unbound, _known.IPipelineBehavior) || IsDefinition(unbound, _known.IStreamPipelineBehavior)) &&
                !IsPlainContainerRegistration(typeOf))
                _opaque = true;
        }
    }

    private void OnObjectCreation(OperationAnalysisContext context)
    {
        if (CqrsRegistrationCalls.IsInGeneratedCode(context.ContainingSymbol)) return;

        if (((IObjectCreationOperation)context.Operation).Type is { } type)
            lock (_gate) NoteMentions(type);
    }

    // A type of the compilation's own that implements or names the serializer or the outbox options (an
    // IConfigureOptions<OutboxOptions>, say) may be registered in a way no call in view shows.
    private void OnNamedType(SymbolAnalysisContext context)
    {
        var type = (INamedTypeSymbol)context.Symbol;
        if (CqrsRegistrationCalls.IsInGeneratedCode(type)) return;

        lock (_gate)
        {
            foreach (var implemented in type.AllInterfaces)
                NoteMentions(implemented);
            for (var baseType = type.BaseType; baseType is not null; baseType = baseType.BaseType)
                NoteMentions(baseType);
        }
    }

    // Records what a type in view names: a built-in behavior a verb registers (then something other than the verb may
    // register it), the notification serializer or the outbox options. Called under the gate.
    private void NoteMentions(ITypeSymbol type)
    {
        switch (type)
        {
            case IArrayTypeSymbol array:
                NoteMentions(array.ElementType);
                return;
            case INamedTypeSymbol named:
                var definition = named.OriginalDefinition;
                if (_builtInBehaviors.Contains(definition, SymbolEqualityComparer.Default)) _opaque = true;
                if (SymbolEqualityComparer.Default.Equals(definition, _serializer)) _serializerInView = true;
                if (SymbolEqualityComparer.Default.Equals(definition, _outboxOptions)) _outboxOptionsInView = true;
                foreach (var argument in named.TypeArguments)
                    NoteMentions(argument);
                return;
        }
    }

    // typeof(IPipelineBehavior<,>) as an argument of a Microsoft.Extensions.DependencyInjection registration (an Add*,
    // TryAdd*, a ServiceDescriptor) whose every Type argument is a typeof: the application registers a behavior it names.
    private bool IsPlainContainerRegistration(ITypeOfOperation typeOf)
    {
        if (typeOf.Parent is not IArgumentOperation { Parent: { } call }) return false;

        var (method, arguments) = call switch
        {
            IInvocationOperation invocation => (invocation.TargetMethod, invocation.Arguments),
            IObjectCreationOperation { Constructor: { } constructor } creation => (constructor, creation.Arguments),
            _ => (null, ImmutableArray<IArgumentOperation>.Empty)
        };
        if (method?.ContainingNamespace?.ToDisplayString() is not ("Microsoft.Extensions.DependencyInjection" or "Microsoft.Extensions.DependencyInjection.Extensions"))
            return false;

        foreach (var argument in arguments)
            if (argument.Parameter?.Type is { Name: "Type", ContainingNamespace.Name: "System" } && argument.Value is not ITypeOfOperation)
                return false;

        return true;
    }

    // The verbs of a builder configuration made of fluent chains on the lambda's parameter: every statement of the lambda
    // is one, every link of it is a verb of ICqrsBuilder (or a builder extension of a CQRSharp package), and no argument of
    // a link can reach the service collection or run the compilation's own code while the builder is configured.
    private bool TryReadVerbs(IOperation configure, ImmutableHashSet<string>.Builder verbs)
    {
        if (configure is not IDelegateCreationOperation { Target: IAnonymousFunctionOperation lambda } ||
            lambda.Symbol.Parameters.Length != 1)
            return false;

        var parameter = lambda.Symbol.Parameters[0];
        foreach (var statement in lambda.Body.Operations)
        {
            IOperation root;
            switch (statement)
            {
                case IExpressionStatementOperation expression:
                    root = expression.Operation;
                    break;
                case IReturnOperation { ReturnedValue: null, IsImplicit: true }:
                    continue;
                default:
                    return false;
            }

            var link = root;
            while (link is IInvocationOperation call)
            {
                if (!TryReadLink(call, verbs)) return false;
                link = CqrsRegistrationCalls.Receiver(call);
            }

            if (link is not IParameterReferenceOperation reference || !SymbolEqualityComparer.Default.Equals(reference.Parameter, parameter))
                return false;
        }

        return true;
    }

    private bool TryReadLink(IInvocationOperation call, ImmutableHashSet<string>.Builder verbs)
    {
        var method = call.TargetMethod;
        if (SymbolEqualityComparer.Default.Equals(method.ContainingType, _builder))
            verbs.Add(method.Name);
        else if (!(method.IsExtensionMethod && KnownBuilderExtensionAssemblies.Contains(method.ContainingAssembly.Identity.Name)))
            return false;

        foreach (var argument in call.Arguments)
        {
            // The builder itself, the extension method's receiver.
            if (method.IsExtensionMethod && argument.Parameter?.Ordinal == 0) continue;
            if (!IsInert(argument)) return false;
        }

        return true;
    }

    // An argument that cannot add to the configuration. A factory the container calls once it is built
    // (Func<IServiceProvider, T>) runs too late to register anything; anything else may run while the builder is
    // configured, so it must neither reach the service collection or a builder nor call into the compilation's own code.
    private bool IsInert(IArgumentOperation argument)
    {
        if (argument.Parameter?.Type is INamedTypeSymbol { IsGenericType: true } parameterType &&
            SymbolEqualityComparer.Default.Equals(parameterType.OriginalDefinition, _func2) &&
            SymbolEqualityComparer.Default.Equals(parameterType.TypeArguments[0], _serviceProvider))
            return true;

        foreach (var operation in argument.Value.DescendantsAndSelf())
        {
            if (operation.Type is { } type && ReachesConfiguration(type)) return false;

            var invoked = operation switch
            {
                IInvocationOperation invocation => invocation.TargetMethod,
                IObjectCreationOperation creation => creation.Constructor,
                IMethodReferenceOperation reference => reference.Method,
                _ => null
            };
            if (invoked is not null && IsOwnCode(invoked)) return false;
        }

        return true;
    }

    // A value through which services could be registered: the service collection, a builder, or a delegate or generic
    // type that carries one.
    private bool ReachesConfiguration(ITypeSymbol type)
    {
        switch (type)
        {
            case IArrayTypeSymbol array:
                return ReachesConfiguration(array.ElementType);
            case INamedTypeSymbol named:
                if (IsOrImplements(named, _serviceCollection) || IsOrImplements(named, _builder)) return true;
                if (named.DelegateInvokeMethod is { } invoke && invoke.Parameters.Any(p => ReachesConfiguration(p.Type))) return true;
                return named.TypeArguments.Any(ReachesConfiguration);
            default:
                return false;
        }
    }

    private static bool IsOrImplements(INamedTypeSymbol type, INamedTypeSymbol? target)
        => target is not null &&
           (SymbolEqualityComparer.Default.Equals(type.OriginalDefinition, target) ||
            type.AllInterfaces.Any(i => SymbolEqualityComparer.Default.Equals(i.OriginalDefinition, target)));

    private static bool IsOwnCode(IMethodSymbol method)
        => method.Locations.Any(l => l.IsInSource) || method.ContainingType?.Locations.Any(l => l.IsInSource) == true;

    private static bool IsDefinition(INamedTypeSymbol unbound, INamedTypeSymbol? definition)
        => definition is not null && SymbolEqualityComparer.Default.Equals(unbound.OriginalDefinition, definition);
}
