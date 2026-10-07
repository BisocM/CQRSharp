using System.Data;
using System.Diagnostics;
using CQRSharp.Pipelines;
using CQRSharp.RabbitMQ;
using CQRSharp.Tests.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CQRSharp.Tests.Integrations.RabbitMQ;

/// <summary>An order notification that crosses the broker, ordered per key; the tests' hosts handle it locally too.</summary>
[NotificationName("tests.rabbit.order-placed", PartitionBy = nameof(Key))]
public sealed record RabbitOrderPlaced(int Seq, string Key) : INotification;

/// <summary>A notification no host handles locally: it only ever leaves the process.</summary>
[NotificationName("tests.rabbit.broadcast")]
public sealed record RabbitBroadcast(int Seq) : INotification;

/// <summary>What one test host's handler of <see cref="RabbitOrderPlaced" /> received, with the trace it ran in.</summary>
public sealed class RabbitReceived
{
    private readonly object _gate = new();
    private readonly List<(RabbitOrderPlaced Order, ActivityTraceId? Trace)> _received = [];
    private readonly List<(int Count, TaskCompletionSource Reached)> _waiters = [];

    /// <summary>Thrown by the handler, after it counted the delivery, until cleared.</summary>
    public Exception? Failure { get; set; }

    public IReadOnlyList<RabbitOrderPlaced> Orders
    {
        get
        {
            lock (_gate) return _received.Select(r => r.Order).ToArray();
        }
    }

    public ActivityTraceId? TraceOf(int seq)
    {
        lock (_gate) return _received.First(r => r.Order.Seq == seq).Trace;
    }

    public void Add(RabbitOrderPlaced order)
    {
        List<TaskCompletionSource> reached;
        lock (_gate)
        {
            _received.Add((order, Activity.Current?.TraceId));
            reached = _waiters.Where(w => w.Count <= _received.Count).Select(w => w.Reached).ToList();
            _waiters.RemoveAll(w => w.Count <= _received.Count);
        }

        foreach (var waiter in reached) waiter.TrySetResult();
    }

    /// <summary>Completes once <paramref name="count" /> orders in all were received.</summary>
    public Task WaitForAsync(int count)
    {
        Task reached;
        lock (_gate)
        {
            if (_received.Count >= count) return Task.CompletedTask;
            var waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add((count, waiter));
            reached = waiter.Task;
        }

        return reached.WaitAsync(TestContext.Current.CancellationToken);
    }
}

public sealed class RabbitOrderPlacedHandler(RabbitReceived received) : INotificationHandler<RabbitOrderPlaced>
{
    public Task Handle(RabbitOrderPlaced notification, CancellationToken cancellationToken)
    {
        received.Add(notification);
        return received.Failure is { } failure ? Task.FromException(failure) : Task.CompletedTask;
    }
}

/// <summary>Places an order in a transaction: the notification it publishes is stored with the transaction's commit.</summary>
public sealed class PlaceRabbitOrderCommand : CommandBase, ITransactionalCommand
{
    public required int Seq { get; init; }
    public required string Key { get; init; }
    public IsolationLevel IsolationLevel { get; init; }
}

public sealed class PlaceRabbitOrderCommandHandler(ICqrsDispatcher cqrs) : ICommandHandler<PlaceRabbitOrderCommand>
{
    public async Task<CommandResult> Handle(PlaceRabbitOrderCommand command, CancellationToken cancellationToken)
    {
        await cqrs.Publish(new RabbitOrderPlaced(command.Seq, command.Key), cancellationToken);
        return CommandResult.FromSuccess();
    }
}

/// <summary>
///     One application of a RabbitMQ test, run as a real host (the outbox processor, the transport's connector and
///     consumers are hosted services): an in-memory outbox unless the test brings a store, the RabbitMQ transport as the test
///     configures it, and the outbox processor polling often, so what the broker brings in is delivered promptly.
/// </summary>
internal sealed class RabbitMqTestHost : IAsyncDisposable
{
    private bool _stopped;

    private RabbitMqTestHost(IHost host, RabbitReceived received, CapturingLoggerProvider logs)
    {
        Host = host;
        Received = received;
        Logs = logs;
    }

    public IHost Host { get; }

    public IServiceProvider Services => Host.Services;

    public RabbitReceived Received { get; }

    public CapturingLoggerProvider Logs { get; }

    public static async Task<RabbitMqTestHost> StartAsync(
        string uri,
        Action<RabbitMqTransportBuilder> transport,
        Action<OutboxStoreBuilder>? outbox = null,
        Action<ICqrsBuilder>? cqrs = null,
        Action<IServiceCollection>? services = null,
        TimeProvider? time = null,
        bool start = true)
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        var received = new RabbitReceived();
        var logs = new CapturingLoggerProvider();
        builder.Services.AddSingleton(received);
        builder.Services.AddSingleton<ILoggerProvider>(logs);
        // Each message's outcome, a declared topology, a returned publish: the Debug lines are what the tests assert on.
        builder.Logging.SetMinimumLevel(LogLevel.Debug);
        builder.Services.AddSingleton<DeliveryProbe>();
        builder.Services.AddSingleton<SharedNotificationLog>();
        if (time is not null) builder.Services.AddSingleton(time);
        builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(30));
        builder.Services.AddCqrsGenerated(b =>
        {
            b.UseOutbox(o =>
            {
                o.ConfigureProcessor(p =>
                {
                    p.PollingInterval = TimeSpan.FromMilliseconds(200);
                    p.Retry.BaseDelay = TimeSpan.FromMilliseconds(200);
                    p.Retry.JitterFactor = 0;
                });
                if (outbox is null) o.UseInMemoryStore();
                else outbox(o);
                o.UseRabbitMq(uri, transport);
            });
            cqrs?.Invoke(b);
        });
        services?.Invoke(builder.Services);

        var host = builder.Build();
        if (start) await host.StartAsync(TestContext.Current.CancellationToken);
        return new RabbitMqTestHost(host, received, logs);
    }

    public async Task PublishAsync(params INotification[] notifications)
    {
        await using var scope = Services.CreateAsyncScope();
        var cqrs = scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>();
        foreach (var notification in notifications)
            await cqrs.Publish(notification, TestContext.Current.CancellationToken);
    }

    public async Task StopAsync()
    {
        if (_stopped) return;
        _stopped = true;
        await Host.StopAsync(CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();

        // What the host logged, for the test's output: a broker test that fails is diagnosed from it.
        if (TestContext.Current.TestOutputHelper is { } output)
            foreach (var (category, entry) in Logs.Entries)
                output.WriteLine($"[{entry.Level}] {entry.EventId.Id} {category}: {entry.Message}{(entry.Exception is null ? "" : " | " + entry.Exception.GetType().Name + ": " + entry.Exception.Message)}");

        if (Host is IAsyncDisposable disposable) await disposable.DisposeAsync();
        else Host.Dispose();
    }
}
