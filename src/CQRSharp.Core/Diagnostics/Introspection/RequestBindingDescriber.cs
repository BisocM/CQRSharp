using CQRSharp.Core.Exceptions;
using CQRSharp.Core.Modules;
using CQRSharp.Core.Pipelines;
using CQRSharp.Core.Registries;
using CQRSharp.Pipelines;
using Microsoft.Extensions.DependencyInjection;

namespace CQRSharp.Core.Diagnostics;

/// <summary>
///     Describes how requests are wired in one scope. A request's route closes the generic entry points over its types;
///     everything a description reports (the CQRDIAG checks, the context-factory probe, which behaviors run and in what
///     order) is decided here, with the rules the executor dispatches by.
/// </summary>
internal sealed class RequestBindingDescriber(IServiceProvider services, IRequestRegistry requests, IContextFactoryRegistry contexts)
{
    private readonly IServiceProviderIsService? _isService = services.GetService<IServiceProviderIsService>();
    private readonly IRequestExceptionHookRegistry? _exceptionHooks = services.GetService<IRequestExceptionHookRegistry>();

    public CqrsRequestBinding Describe<TRequest, TResult>() where TRequest : IRequest
        => Describe(typeof(TRequest), typeof(TResult), static sp => PipelineBehaviors.Resolve<TRequest, TResult>(sp), "pipeline behaviors",
            HasValidators<TRequest>());

    public CqrsRequestBinding DescribeStream<TRequest, TItem>() where TRequest : IStreamRequest<TItem>
        => Describe(typeof(TRequest), typeof(IAsyncEnumerable<TItem>), static sp => PipelineBehaviors.ResolveStream<TRequest, TItem>(sp),
            "stream pipeline behaviors", HasValidators<TRequest>());

    // Answered from the registrations, without constructing a validator: registered for the request itself, or
    // discovered by a module. Without registration queries nothing is proven, and nothing is reported.
    private bool HasValidators<TRequest>() where TRequest : IRequest
        => _isService is not null &&
           (_isService.IsService(typeof(IRequestValidator<TRequest>)) ||
            (_isService is IServiceProviderIsKeyedService keyed &&
             keyed.IsKeyedService(typeof(IRequestValidator<TRequest>), DiscoveredServices.Key)));

    private CqrsRequestBinding Describe(
        Type requestType,
        Type responseType,
        Func<IServiceProvider, object[]> resolveBehaviors,
        string behaviorKind,
        bool hasValidators)
    {
        var issues = new List<CqrsBindingIssue>();

        // Without metadata the request cannot be dispatched at all, so it has no handler or context to describe or probe.
        Type? handlerType = null;
        Type? contextType = null;
        if (requests.TryGetRequestMetadata(requestType, out var metadata))
        {
            handlerType = metadata.HandlerType;
            contextType = metadata.ContextType;
            ProbeContextFactory(contextType, issues);
        }
        else
        {
            issues.Add(Error("CQRDIAG001", $"No request metadata registered for '{requestType.FullName}'."));
        }

        // Resolved inside the try: a behavior (or a dependency of one) that cannot be constructed is what CQRDIAG004 reports.
        object[] behaviors;
        try
        {
            behaviors = resolveBehaviors(services);
        }
        catch (Exception ex)
        {
            issues.Add(Error("CQRDIAG004", $"Failed to resolve {behaviorKind} for '{requestType.FullName}': {ex.Message}"));
            behaviors = [];
        }

        var exemptions = metadata?.PipelineExemptions ?? [];
        var pipeline = new List<(CqrsPipelineBehaviorBinding Binding, object Behavior)>(behaviors.Length);
        var exempted = new List<(CqrsPipelineBehaviorBinding Binding, object Behavior)>();
        foreach (var behavior in behaviors)
        {
            var type = behavior.GetType();
            var priority = behavior is IPrioritizedPipelineBehavior prioritized
                ? prioritized.PipelineExecutionPriority
                : IPrioritizedPipelineBehavior.DefaultPriority;
            (PipelineExemptions.IsExempted(type, exemptions) ? exempted : pipeline).Add((new CqrsPipelineBehaviorBinding(type, priority), behavior));
        }

        // What the request brings along only works through its behavior: a validator without the validation behavior
        // lets unvalidated input reach the handler, and an exception hook without the exception-handling behavior never
        // runs. An exempted behavior is a deliberate opt-out, and a pipeline that could not be resolved (CQRDIAG004) is
        // unknown rather than missing.
        if (!issues.Any(i => i.Code == "CQRDIAG004"))
        {
            var wired = pipeline.Concat(exempted).Select(entry => entry.Binding.BehaviorType).ToList();
            if (hasValidators && !wired.Any(typeof(ICqrsValidationBehaviorMarker).IsAssignableFrom))
                issues.Add(Warning("CQRDIAG005",
                    $"Request '{requestType.FullName}' has validators, but the validation behavior is not in its pipeline, so they " +
                    "never run and its input reaches the handler unvalidated. Register CQRSharp without UseValidation(false)."));
            if (_exceptionHooks?.TryGetInvoker(requestType, out _) == true &&
                !wired.Any(typeof(ICqrsExceptionHandlingBehaviorMarker).IsAssignableFrom))
                issues.Add(Warning("CQRDIAG006",
                    $"Request '{requestType.FullName}' has exception actions or handlers, but the exception-handling behavior is " +
                    "not in its pipeline, so they never run. Register CQRSharp without UseExceptionHandling(false)."));
        }

        return new CqrsRequestBinding(
            requestType,
            responseType,
            handlerType,
            contextType,
            Array.ConvertAll(exemptions, exemption => exemption.ExemptedPipeline),
            Interceptors(metadata?.PreHandlers ?? [], static attribute => attribute.PreHandlerExecutionPriority),
            Interceptors(metadata?.PostHandlers ?? [], static attribute => attribute.PostHandlerExecutionPriority),
            InExecutionOrder(pipeline),
            InExecutionOrder(exempted),
            issues);
    }

    private void ProbeContextFactory(Type contextType, List<CqrsBindingIssue> issues)
    {
        object? factory;
        try
        {
            factory = contexts.TryGetSource(contextType)?.ResolveFactory(services);
        }
        catch (Exception ex)
        {
            issues.Add(Error("CQRDIAG003", $"Failed to resolve context factory for context type '{contextType.FullName}': {ex.Message}"));
            return;
        }

        if (factory is null)
            issues.Add(Error("CQRDIAG003", $"No context factory registered for context type '{contextType.FullName}'."));
    }

    private static CqrsInterceptorBinding[] Interceptors<TAttribute>(TAttribute[] attributes, Func<TAttribute, int> priorityOf)
        where TAttribute : class
    {
        if (attributes.Length == 0) return [];

        var ordered = (TAttribute[])attributes.Clone();
        Array.Sort(ordered, (x, y) => PriorityOrdering.Compare(priorityOf(x), priorityOf(y), x, y));
        return Array.ConvertAll(ordered, attribute => new CqrsInterceptorBinding(attribute.GetType(), priorityOf(attribute)));
    }

    private static CqrsPipelineBehaviorBinding[] InExecutionOrder(List<(CqrsPipelineBehaviorBinding Binding, object Behavior)> behaviors)
    {
        behaviors.Sort((x, y) => PriorityOrdering.Compare(x.Binding.Priority, y.Binding.Priority, x.Behavior, y.Behavior));
        return behaviors.ConvertAll(entry => entry.Binding).ToArray();
    }

    private static CqrsBindingIssue Error(string code, string message) => new(CqrsBindingIssueSeverity.Error, code, message);

    private static CqrsBindingIssue Warning(string code, string message) => new(CqrsBindingIssueSeverity.Warning, code, message);
}
