using System.Collections.Concurrent;
using CQRSharp.Sample.Domain.Events;

namespace CQRSharp.Sample.Infrastructure.SelfTest;

public sealed class SampleDiagnostics
{
    private readonly ConcurrentDictionary<Type, int> _loggedRequests = new();
    private readonly ConcurrentDictionary<Type, int> _notificationPipelineBefore = new();
    private readonly ConcurrentDictionary<Type, int> _notificationPipelineAfter = new();
    private readonly ConcurrentDictionary<Type, int> _interceptorPre = new();
    private readonly ConcurrentDictionary<Type, int> _interceptorPost = new();
    private readonly ConcurrentDictionary<Type, int> _exceptionActions = new();
    private readonly ConcurrentDictionary<Type, int> _exceptionHandlers = new();

    private readonly ConcurrentQueue<UserCreatedNotification> _userCreated = new();
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<UserCreatedNotification>> _userCreatedWaiters = new();

    private int _validatedCommandHandlerInvocations;

    public void RecordLoggedRequest(Type requestType)
        => _loggedRequests.AddOrUpdate(requestType, 1, (_, count) => count + 1);

    public int GetLoggedRequestCount(Type requestType)
        => _loggedRequests.TryGetValue(requestType, out var count) ? count : 0;

    public void RecordNotificationPipelineBefore(Type notificationType)
        => _notificationPipelineBefore.AddOrUpdate(notificationType, 1, (_, count) => count + 1);

    public void RecordNotificationPipelineAfter(Type notificationType)
        => _notificationPipelineAfter.AddOrUpdate(notificationType, 1, (_, count) => count + 1);

    public int GetNotificationPipelineBeforeCount(Type notificationType)
        => _notificationPipelineBefore.TryGetValue(notificationType, out var count) ? count : 0;

    public int GetNotificationPipelineAfterCount(Type notificationType)
        => _notificationPipelineAfter.TryGetValue(notificationType, out var count) ? count : 0;

    public void RecordInterceptorPre(Type requestType)
        => _interceptorPre.AddOrUpdate(requestType, 1, (_, count) => count + 1);

    public void RecordInterceptorPost(Type requestType)
        => _interceptorPost.AddOrUpdate(requestType, 1, (_, count) => count + 1);

    public int GetInterceptorPreCount(Type requestType)
        => _interceptorPre.TryGetValue(requestType, out var count) ? count : 0;

    public int GetInterceptorPostCount(Type requestType)
        => _interceptorPost.TryGetValue(requestType, out var count) ? count : 0;

    public void RecordExceptionAction(Type requestType)
        => _exceptionActions.AddOrUpdate(requestType, 1, (_, count) => count + 1);

    public int GetExceptionActionCount(Type requestType)
        => _exceptionActions.TryGetValue(requestType, out var count) ? count : 0;

    public void RecordExceptionHandler(Type requestType)
        => _exceptionHandlers.AddOrUpdate(requestType, 1, (_, count) => count + 1);

    public int GetExceptionHandlerCount(Type requestType)
        => _exceptionHandlers.TryGetValue(requestType, out var count) ? count : 0;

    public void RecordUserCreated(UserCreatedNotification notification)
    {
        _userCreated.Enqueue(notification);
        if (_userCreatedWaiters.TryRemove(notification.UserId, out var waiter))
            waiter.TrySetResult(notification);
    }

    public Task<UserCreatedNotification> WaitForUserCreatedAsync(Guid userId, TimeSpan timeout, CancellationToken cancellationToken)
    {
        foreach (var existing in _userCreated)
        {
            if (existing.UserId == userId)
                return Task.FromResult(existing);
        }

        var tcs = new TaskCompletionSource<UserCreatedNotification>(TaskCreationOptions.RunContinuationsAsynchronously);
        _userCreatedWaiters[userId] = tcs;

        return tcs.Task.WaitAsync(timeout, cancellationToken);
    }

    public void RecordValidatedCommandHandlerInvocation()
        => Interlocked.Increment(ref _validatedCommandHandlerInvocations);

    public int GetValidatedCommandHandlerInvocationCount()
        => Volatile.Read(ref _validatedCommandHandlerInvocations);
}
