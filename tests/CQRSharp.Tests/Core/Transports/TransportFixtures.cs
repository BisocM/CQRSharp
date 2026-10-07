using System.Collections.Concurrent;
using System.Diagnostics;
using CQRSharp.Transports;

namespace CQRSharp.Tests.Core;

/// <summary>A notification no handler of the tests receives: only a transport can take it anywhere.</summary>
[NotificationName("tests.transport.forwarded", PartitionBy = nameof(Key))]
public sealed record ForwardedNotification(int Seq, string? Key) : INotification;

/// <summary>A notification a local handler receives and a transport may forward too.</summary>
[NotificationName("tests.transport.shared")]
public sealed record SharedNotification(int Seq) : INotification;

/// <summary>A notification without a stable name: it can never be stored, so never forwarded.</summary>
public sealed record UnnamedForwardedNotification(int Seq) : INotification;

/// <summary>What the local handler of <see cref="SharedNotification" /> received.</summary>
public sealed class SharedNotificationLog
{
    public ConcurrentQueue<SharedNotification> Received { get; } = new();
}

public sealed class SharedNotificationHandler(SharedNotificationLog log) : INotificationHandler<SharedNotification>
{
    public Task Handle(SharedNotification notification, CancellationToken cancellationToken)
    {
        log.Received.Enqueue(notification);
        return Task.CompletedTask;
    }
}

/// <summary>
///     A notification transport a test scripts: it routes the names it is given, records every send (with the span that
///     was current), and answers each send as <see cref="OnSend" /> says.
/// </summary>
internal sealed class ScriptedTransport(string name = ScriptedTransport.DefaultName, params string[] routedNames) : INotificationTransport
{
    public const string DefaultName = "test-transport";

    private readonly HashSet<string> _routed = [.. routedNames];

    public string Name => name;

    public NotificationTransportDeclaration Declaration { get; init; } = NotificationTransportDeclaration.None;

    /// <summary>Every routing question, in the order asked.</summary>
    public ConcurrentQueue<(string Name, Type Type)> RouteQuestions { get; } = new();

    /// <summary>Every send, in the order made.</summary>
    public ConcurrentQueue<OutboundNotification> Sends { get; } = new();

    /// <summary>The span current during each send.</summary>
    public ConcurrentQueue<Activity?> SendActivities { get; } = new();

    /// <summary>What a send returns; sent, by default.</summary>
    public Func<OutboundNotification, CancellationToken, Task<TransportSendResult>> OnSend { get; set; }
        = (_, _) => Task.FromResult(TransportSendResult.Sent);

    public bool Routes(string notificationName, Type notificationType)
    {
        RouteQuestions.Enqueue((notificationName, notificationType));
        return _routed.Contains(notificationName);
    }

    public Task<TransportSendResult> SendAsync(OutboundNotification message, CancellationToken cancellationToken)
    {
        Sends.Enqueue(message);
        SendActivities.Enqueue(Activity.Current);
        return OnSend(message, cancellationToken);
    }
}

/// <summary>Captures every log entry of the container it is registered in, with its category, and wakes those waiting for one.</summary>
internal sealed class CapturingLoggerProvider : Microsoft.Extensions.Logging.ILoggerProvider
{
    private readonly ConcurrentDictionary<string, CategoryLogger> _loggers = new();
    private readonly object _gate = new();
    private readonly List<(int EventId, int Count, TaskCompletionSource Reached)> _waiters = [];

    public IReadOnlyList<(string Category, CQRSharp.Tests.Shared.CapturedLogEntry Entry)> Entries
        => _loggers.SelectMany(l => l.Value.Inner.Entries.Select(e => (l.Key, e))).ToArray();

    /// <summary>The entries with event id <paramref name="eventId" />.</summary>
    public IReadOnlyList<CQRSharp.Tests.Shared.CapturedLogEntry> WithId(int eventId)
        => Entries.Where(e => e.Entry.EventId.Id == eventId).Select(e => e.Entry).ToArray();

    /// <summary>Completes once <paramref name="count" /> entries with event id <paramref name="eventId" /> were logged.</summary>
    public Task WaitForAsync(int eventId, int count = 1)
    {
        Task reached;
        lock (_gate)
        {
            if (WithId(eventId).Count >= count) return Task.CompletedTask;
            var waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add((eventId, count, waiter));
            reached = waiter.Task;
        }

        return reached.WaitAsync(TestContext.Current.CancellationToken);
    }

    public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName) => _loggers.GetOrAdd(categoryName, _ => new CategoryLogger(this));

    public void Dispose()
    {
    }

    private void Logged()
    {
        List<TaskCompletionSource> reached;
        lock (_gate)
        {
            reached = _waiters.Where(w => WithId(w.EventId).Count >= w.Count).Select(w => w.Reached).ToList();
            _waiters.RemoveAll(w => reached.Contains(w.Reached));
        }

        foreach (var waiter in reached) waiter.TrySetResult();
    }

    private sealed class CategoryLogger(CapturingLoggerProvider owner) : Microsoft.Extensions.Logging.ILogger
    {
        public CQRSharp.Tests.Shared.CapturingLogger<CategoryLogger> Inner { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => Inner.BeginScope(state);

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Inner.Log(logLevel, eventId, state, exception, formatter);
            owner.Logged();
        }
    }
}
