using CQRSharp.Abstractions.Interfaces.Markers.Request;

namespace CQRSharp.Abstractions.Attributes.Pipelines;

/// <summary>
///     Base class for an outcome-aware post-handler attribute. Derive from this and override the single
///     <see cref="OnAfterHandle(IRequest, RequestOutcome, IServiceProvider, CancellationToken)" /> to run after the
///     handler with the value it returned or the exception it threw — for example, to audit an operation by the verdict
///     it returned (a successful login vs. a returned "bad credentials"). The dispatcher runs it on both the success and
///     the exception paths. Apply it to a request type exactly like any other post-handler attribute.
/// </summary>
public abstract class OutcomeAwarePostHandlerAttribute : Attribute, IPostHandlerAttribute, IPostHandlerOutcomeAware
{
    /// <inheritdoc cref="IPostHandlerAttribute.PostHandlerExecutionPriority" />
    public abstract int PostHandlerExecutionPriority { get; }

    /// <summary>Runs after the handler with the request and its <see cref="RequestOutcome" /> (the returned value or the thrown exception).</summary>
    /// <param name="request">The request that was handled.</param>
    /// <param name="outcome">The outcome of the handler: its returned value, or the exception it threw.</param>
    /// <param name="serviceProvider">The scoped service provider.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    public abstract Task OnAfterHandle(IRequest request, RequestOutcome outcome, IServiceProvider serviceProvider, CancellationToken cancellationToken);

    // The plain post-handler entry point is never used for this type (the dispatcher prefers the outcome-aware one); it
    // forwards for completeness so the type still satisfies IPostHandlerAttribute.
    Task IPostHandlerAttribute.OnAfterHandle(IRequest request, IServiceProvider serviceProvider, CancellationToken cancellationToken)
        => OnAfterHandle(request, default, serviceProvider, cancellationToken);
}
