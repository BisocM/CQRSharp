namespace CQRSharp.Abstractions.Interfaces.Markers.Request;

/// <summary>
///     Opt-in marker interface that makes a request eligible for automatic retries by the resilience behavior.
/// </summary>
/// <remarks>
///     Retrying re-invokes the handler, so only <b>idempotent</b> requests should implement this interface. Requests
///     that are not idempotent (most commands that mutate state) must not be retried automatically and therefore must
///     not implement <see cref="IRetryableRequest" />; the resilience behavior will propagate their first failure
///     instead of replaying side effects. Caller cancellation (<see cref="System.OperationCanceledException" />) and
///     timeouts (<see cref="System.TimeoutException" />) are never retried, even for retryable requests.
/// </remarks>
public interface IRetryableRequest : IRequest;
