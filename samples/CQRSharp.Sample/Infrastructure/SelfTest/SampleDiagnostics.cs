using System.Collections.Concurrent;
using CQRSharp.Sample.Domain.Events;

namespace CQRSharp.Sample.Infrastructure.SelfTest;

/// <summary>
///     What the sample's handlers, behaviors and hooks observed, for the self-test to assert on. The outbox processor
///     delivers on its own thread, so every observation the self-test waits for is a latch: whichever of the recorder and
///     the waiter comes first creates it, and the other finds it, so neither order loses the signal.
/// </summary>
public sealed class SampleDiagnostics
{
    private readonly ConcurrentDictionary<Type, int> _requests = new();
    private readonly ConcurrentDictionary<Type, int> _streamBehaviorRuns = new();
    private readonly ConcurrentDictionary<Type, int> _notificationPipelineBefore = new();
    private readonly ConcurrentDictionary<Type, int> _notificationPipelineAfter = new();
    private readonly ConcurrentDictionary<Type, int> _interceptorPre = new();
    private readonly ConcurrentDictionary<Type, int> _interceptorPost = new();
    private readonly ConcurrentDictionary<Type, int> _exceptionActions = new();
    private readonly ConcurrentDictionary<Type, int> _exceptionHandlers = new();
    private readonly ConcurrentDictionary<string, int> _runs = new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<Type, TaskCompletionSource> _notificationPipelineCompleted = new();
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<UserCreatedDelivery>> _usersCreated = new();
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<OrderPlacedNotification>> _ordersPlaced = new();

    public void RecordRequest(Type requestType) => Increment(_requests, requestType);

    public int GetRequestCount(Type requestType) => _requests.GetValueOrDefault(requestType);

    public void RecordStreamBehavior(Type requestType) => Increment(_streamBehaviorRuns, requestType);

    public int GetStreamBehaviorCount(Type requestType) => _streamBehaviorRuns.GetValueOrDefault(requestType);

    public void RecordNotificationPipelineBefore(Type notificationType) => Increment(_notificationPipelineBefore, notificationType);

    public void RecordNotificationPipelineAfter(Type notificationType)
    {
        Increment(_notificationPipelineAfter, notificationType);
        Latch(_notificationPipelineCompleted, notificationType).TrySetResult();
    }

    public int GetNotificationPipelineBeforeCount(Type notificationType) => _notificationPipelineBefore.GetValueOrDefault(notificationType);

    public int GetNotificationPipelineAfterCount(Type notificationType) => _notificationPipelineAfter.GetValueOrDefault(notificationType);

    /// <summary>Completes once the notification pipeline has run to its end for a notification of this type.</summary>
    public Task WaitForNotificationPipelineAsync(Type notificationType, TimeSpan timeout, CancellationToken cancellationToken)
        => Latch(_notificationPipelineCompleted, notificationType).Task.WaitAsync(timeout, cancellationToken);

    public void RecordInterceptorPre(Type requestType) => Increment(_interceptorPre, requestType);

    public void RecordInterceptorPost(Type requestType, RequestOutcome outcome)
    {
        if (!outcome.Threw) Increment(_interceptorPost, requestType);
    }

    public int GetInterceptorPreCount(Type requestType) => _interceptorPre.GetValueOrDefault(requestType);

    /// <summary>How many times a post-handler saw the request succeed.</summary>
    public int GetInterceptorPostCount(Type requestType) => _interceptorPost.GetValueOrDefault(requestType);

    public void RecordExceptionAction(Type requestType) => Increment(_exceptionActions, requestType);

    public int GetExceptionActionCount(Type requestType) => _exceptionActions.GetValueOrDefault(requestType);

    public void RecordExceptionHandler(Type requestType) => Increment(_exceptionHandlers, requestType);

    public int GetExceptionHandlerCount(Type requestType) => _exceptionHandlers.GetValueOrDefault(requestType);

    /// <summary>Counts one run of the work <paramref name="key" /> names and returns how many there have been.</summary>
    public int CountRun(string key) => _runs.AddOrUpdate(key, 1, static (_, count) => count + 1);

    public int GetRunCount(string key) => _runs.GetValueOrDefault(key);

    public void RecordUserCreated(UserCreatedNotification notification, Guid handlerScopeId)
        => Latch(_usersCreated, notification.UserId).TrySetResult(new UserCreatedDelivery(notification, handlerScopeId));

    public Task<UserCreatedDelivery> WaitForUserCreatedAsync(Guid userId, TimeSpan timeout, CancellationToken cancellationToken)
        => Latch(_usersCreated, userId).Task.WaitAsync(timeout, cancellationToken);

    public void RecordOrderPlaced(OrderPlacedNotification notification)
        => Latch(_ordersPlaced, notification.OrderId).TrySetResult(notification);

    public Task<OrderPlacedNotification> WaitForOrderPlacedAsync(Guid orderId, TimeSpan timeout, CancellationToken cancellationToken)
        => Latch(_ordersPlaced, orderId).Task.WaitAsync(timeout, cancellationToken);

    private static void Increment<TKey>(ConcurrentDictionary<TKey, int> counts, TKey key) where TKey : notnull
        => counts.AddOrUpdate(key, 1, static (_, count) => count + 1);

    private static TaskCompletionSource Latch<TKey>(ConcurrentDictionary<TKey, TaskCompletionSource> latches, TKey key)
        where TKey : notnull
        => latches.GetOrAdd(key, static _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

    private static TaskCompletionSource<T> Latch<TKey, T>(ConcurrentDictionary<TKey, TaskCompletionSource<T>> latches, TKey key)
        where TKey : notnull
        => latches.GetOrAdd(key, static _ => new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously));
}

/// <summary>A delivered <see cref="UserCreatedNotification" /> and the id of the DI scope its handler ran in.</summary>
public sealed record UserCreatedDelivery(UserCreatedNotification Notification, Guid HandlerScopeId);
