using CQRSharp.Abstractions.Interfaces.Markers.Request;

namespace CQRSharp.Abstractions.Attributes.Pipelines;

/// <summary>
///     A post-handler that needs the request's <see cref="RequestOutcome" /> — the returned value or the thrown
///     exception — not just the request. Implement this (most easily by deriving from
///     <see cref="OutcomeAwarePostHandlerAttribute" />) when a post-handler must classify on what the handler produced,
///     such as auditing a login by the verdict it returned. When a post-handler implements this, the dispatcher invokes
///     it in preference to the plain <see cref="IPostHandlerAttribute.OnAfterHandle(IRequest, IServiceProvider, CancellationToken)" />,
///     and — unlike the plain post-handler, which runs only on success — also runs it when the handler threw (with the
///     exception on <see cref="RequestOutcome" />).
/// </summary>
public interface IPostHandlerOutcomeAware
{
    /// <summary>Runs after the handler with the request and its <see cref="RequestOutcome" /> (the returned value or the thrown exception).</summary>
    /// <param name="request">The request that was handled.</param>
    /// <param name="outcome">The outcome of the handler: its returned value, or the exception it threw.</param>
    /// <param name="serviceProvider">The scoped service provider.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    Task OnAfterHandle(IRequest request, RequestOutcome outcome, IServiceProvider serviceProvider, CancellationToken cancellationToken);
}
