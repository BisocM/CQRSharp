namespace CQRSharp;

/// <summary>
///     Opt-in marker interface that makes a request eligible for automatic retries by the resilience behavior.
/// </summary>
/// <remarks>
///     Retrying re-invokes the handler, so only <b>idempotent</b> requests should implement this interface. Requests
///     that are not idempotent (most commands that mutate state) must not be retried automatically and therefore must
///     not implement <see cref="IRetryableRequest" />; the resilience behavior will propagate their first failure
///     instead of replaying side effects. Even for a retryable request, a failure that another attempt cannot change is
///     never retried: caller cancellation, the timeout behavior's own <c>RequestTimeoutException</c>, a rate-limit
///     rejection, a duplicate or mismatched idempotency key, and a validation failure. Any other exception is retried,
///     including a <see cref="System.TimeoutException" /> or <see cref="System.OperationCanceledException" /> raised by a
///     dependency.
/// </remarks>
public interface IRetryableRequest : IRequest;