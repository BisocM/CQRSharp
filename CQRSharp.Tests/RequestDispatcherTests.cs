using System.Collections.Concurrent;
using CQRSharp.Abstractions.Data.Attributes.Pipelines;
using CQRSharp.Abstractions.Data.Interfaces.Context;
using CQRSharp.Abstractions.Data.Interfaces.Handlers;
using CQRSharp.Abstractions.Data.Interfaces.Markers.Command;
using CQRSharp.Abstractions.Data.Interfaces.Markers.Query;
using CQRSharp.Abstractions.Data.Interfaces.Markers.Request;
using CQRSharp.Abstractions.Data.Interfaces.Notifications;
using CQRSharp.Abstractions.Data.Models.Commands;
using CQRSharp.Abstractions.Data.Models.Requests;
using CQRSharp.Core.Background.TaskQueue;
using CQRSharp.Core.Background.TaskQueue.Telemetry;
using CQRSharp.Core.Caching.Contexts;
using CQRSharp.Core.Caching.Handlers;
using CQRSharp.Core.Caching.Pipelines;
using CQRSharp.Core.Caching.Requests;
using CQRSharp.Core.Factories;
using CQRSharp.Core.Notifications;
using CQRSharp.Core.Notifications.Types;
using CQRSharp.Core.Options;
using CQRSharp.Core.Options.Enums;
using CQRSharp.Core.Requests;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CQRSharp.Tests;

// Dummy command with attributes for testing the full pipeline
[DummyPreHandler]
[DummyPostHandler]
public class DummyCommand : CommandBase
{
}

public class DummyCommandHandler : ICommandHandler<DummyCommand>
{
    public bool Handled { get; private set; }

    public Task<CommandResult> Handle(DummyCommand command, CancellationToken ct)
    {
        Handled = true;
        return Task.FromResult(CommandResult.FromSuccess());
    }
}

// Dummy query and handler
public class DummyQuery : QueryBase<int>
{
}

public class DummyQueryHandler : IQueryHandler<DummyQuery, int>
{
    public bool Handled { get; private set; }

    public Task<int> Handle(DummyQuery query, CancellationToken ct)
    {
        Handled = true;
        return Task.FromResult(42);
    }
}

// Pre- and post-handler attributes
internal sealed class DummyPreHandler : Attribute, IPreHandlerAttribute
{
    public bool Invoked { get; set; }
    public int PreHandlerExecutionPriority => 0;

    public Task OnBeforeHandle(IRequest req, IServiceProvider sp, CancellationToken ct)
    {
        Invoked = true;
        return Task.CompletedTask;
    }
}

internal sealed class DummyPostHandler : Attribute, IPostHandlerAttribute
{
    public bool Invoked { get; set; }
    public int PostHandlerExecutionPriority => 0;

    public Task OnAfterHandle(IRequest req, IServiceProvider sp, CancellationToken ct)
    {
        Invoked = true;
        return Task.CompletedTask;
    }
}

// Notification handlers
internal class CommandInitiatedHandler : INotificationHandler<CommandInitiatedNotification>
{
    public bool Invoked { get; private set; }

    public Task Handle(CommandInitiatedNotification notification, CancellationToken ct)
    {
        Invoked = true;
        return Task.CompletedTask;
    }
}

internal class CommandCompletedHandler : INotificationHandler<CommandCompletedNotification>
{
    public bool Invoked { get; private set; }

    public Task Handle(CommandCompletedNotification notification, CancellationToken ct)
    {
        Invoked = true;
        return Task.CompletedTask;
    }
}

internal class QueryInitiatedHandler : INotificationHandler<QueryInitiatedNotification<int>>
{
    public bool Invoked { get; private set; }

    public Task Handle(QueryInitiatedNotification<int> notification, CancellationToken ct)
    {
        Invoked = true;
        return Task.CompletedTask;
    }
}

internal class QueryCompletedHandler : INotificationHandler<QueryCompletedNotification<int>>
{
    public bool Invoked { get; private set; }
    public object? Result { get; private set; }

    public Task Handle(QueryCompletedNotification<int> notification, CancellationToken ct)
    {
        Invoked = true;
        Result = notification.Result;
        return Task.CompletedTask;
    }
}

// A fake IHostApplicationLifetime to supply shutdown token
internal class TestHostApplicationLifetime : IHostApplicationLifetime
{
    private readonly CancellationTokenSource _cts = new();
    public CancellationToken ApplicationStarted => CancellationToken.None;
    public CancellationToken ApplicationStopping => _cts.Token;
    public CancellationToken ApplicationStopped => CancellationToken.None;
    public void StopApplication() => _cts.Cancel();
}

public class RequestDispatcherTests
{
    private readonly CommandCompletedHandler _cmdDoneNotif;
    private readonly DummyCommandHandler _cmdHandler;
    private readonly CommandInitiatedHandler _cmdInitNotif;
    private readonly DummyPostHandler _post;
    private readonly DummyPreHandler _pre;
    private readonly QueryCompletedHandler _qryDoneNotif;
    private readonly DummyQueryHandler _qryHandler;
    private readonly QueryInitiatedHandler _qryInitNotif;
    private readonly IBackgroundTaskQueue _queue;
    private readonly ServiceProvider _serviceProvider;

    public RequestDispatcherTests()
    {
        _cmdHandler = new DummyCommandHandler();
        _cmdInitNotif = new CommandInitiatedHandler();
        _cmdDoneNotif = new CommandCompletedHandler();
        _qryHandler = new DummyQueryHandler();
        _qryInitNotif = new QueryInitiatedHandler();
        _qryDoneNotif = new QueryCompletedHandler();
        _pre = new DummyPreHandler();
        _post = new DummyPostHandler();

        var services = new ServiceCollection();

        // Logging
        services.AddSingleton<ILoggerFactory>(_ => LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.None)));
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));

        // Host lifetime
        var lifetime = new TestHostApplicationLifetime();
        services.AddSingleton<IHostApplicationLifetime>(lifetime);

        // Options
        services.AddSingleton<IOptions<DispatcherOptions>>(_ => Options.Create(new DispatcherOptions()));
        services.AddSingleton<IOptions<BackgroundTaskQueueOptions>>(_ => Options.Create(new BackgroundTaskQueueOptions()));
        services.AddSingleton<IOptions<OutboxOptions>>(_ => Options.Create(new OutboxOptions()));

        // The main dispatcher is scoped and decides routing; the direct dispatcher is a singleton for immediate execution.
        services.AddScoped<INotificationDispatcher, NotificationDispatcher>();
        services.AddSingleton<IDirectNotificationDispatcher, DirectNotificationDispatcher>();

        // Notification handlers
        services.AddSingleton<INotificationHandler<CommandInitiatedNotification>>(_ => _cmdInitNotif);
        services.AddSingleton<INotificationHandler<CommandCompletedNotification>>(_ => _cmdDoneNotif);
        services.AddSingleton<INotificationHandler<QueryInitiatedNotification<int>>>(_ => _qryInitNotif);
        services.AddSingleton<INotificationHandler<QueryCompletedNotification<int>>>(_ => _qryDoneNotif);

        // Manually create the direct dispatcher for the queue, as its notifications should never be outboxed.
        var directDispatcher = new DirectNotificationDispatcher(services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>());

        _queue = new BackgroundTaskQueue(
            Options.Create(new BackgroundTaskQueueOptions()),
            directDispatcher,
            lifetime,
            new NoOpMetricsReporter(),
            new Logger<BackgroundTaskQueue>(new LoggerFactory())
        );
        services.AddSingleton(_queue);

        // Request registry
        var reqMap = new ConcurrentDictionary<Type, RequestMetadata>
        {
            [typeof(DummyCommand)] = new(
                typeof(DummyCommand),
                typeof(DummyCommandHandler),
                [_pre],
                [_post],
                Array.Empty<PipelineExemptionAttribute>(),
                Array.Empty<PropertySensitivity>(),
                typeof(CommandResult),
                typeof(RequestContextBase)
            ),
            [typeof(DummyQuery)] = new(
                typeof(DummyQuery),
                typeof(DummyQueryHandler),
                Array.Empty<IPreHandlerAttribute>(),
                Array.Empty<IPostHandlerAttribute>(),
                Array.Empty<PipelineExemptionAttribute>(),
                Array.Empty<PropertySensitivity>(),
                typeof(int),
                typeof(RequestContextBase)
            )
        };
        services.AddSingleton<IRequestRegistry>(new RequestRegistry(reqMap));

        // Handler registry
        var handlerMap = new ConcurrentDictionary<Type, HandlerInvokerDelegate>
        {
            [typeof(DummyCommand)] = (handler, req, ct) =>
                ((DummyCommandHandler)handler).Handle((DummyCommand)req, ct).ContinueWith<object>(t => t.Result, ct),
            [typeof(DummyQuery)] = (handler, req, ct) =>
                ((DummyQueryHandler)handler).Handle((DummyQuery)req, ct).ContinueWith<object>(t => t.Result, ct)
        };
        services.AddSingleton<IHandlerRegistry>(new HandlerRegistry(handlerMap));

        // Pipeline registry (empty)
        services.AddSingleton<IPipelineRegistry>(
            new PipelineRegistry(new ConcurrentDictionary<Type, PipelineBuilderDelegate>()));

        // Context factory registry and factory
        var contextFactory = new DefaultRequestContextFactory();
        services.AddSingleton<IContextFactoryRegistry>(new ContextFactoryRegistry(
            new ConcurrentDictionary<Type, Func<IServiceProvider, object>>
            {
                [typeof(RequestContextBase)] = _ => contextFactory
            }));
        services.AddTransient<IRequestContextFactory, DefaultRequestContextFactory>();

        // Concrete handlers
        services.AddTransient<DummyCommandHandler>(_ => _cmdHandler);
        services.AddTransient<DummyQueryHandler>(_ => _qryHandler);

        // The main dispatcher should be resolved from a scope.
        services.AddScoped<IRequestDispatcher, RequestDispatcher>();

        // Build final service provider
        _serviceProvider = services.BuildServiceProvider();
    }

    private IRequestDispatcher BuildDispatcher(RunMode mode)
    {
        // Create a scope from the main provider to correctly resolve scoped services.
        var scope = _serviceProvider.CreateScope();

        // Manually set the run mode for the specific test scenario.
        var dispatcherOptions = scope.ServiceProvider.GetRequiredService<IOptions<DispatcherOptions>>();
        dispatcherOptions.Value.RunMode = mode;

        return scope.ServiceProvider.GetRequiredService<IRequestDispatcher>();
    }

    [Fact(DisplayName = "Sync Command dispatches command, handlers, attributes, and notifications")]
    public async Task SyncCommand_DispatchesCorrectly()
    {
        var dispatcher = BuildDispatcher(RunMode.Sync);
        var cmd = new DummyCommand();

        var result = await dispatcher.ExecuteCommand(cmd);

        Assert.True(result.IsSuccess, "Expected CommandResult.IsSuccess == true");
        Assert.True(_cmdHandler.Handled, "Expected DummyCommandHandler.Handle(...) to be called");
        Assert.True(_pre.Invoked, "Expected pre-handle attribute to run");
        Assert.True(_post.Invoked, "Expected post-handle attribute to run");
        Assert.True(_cmdInitNotif.Invoked, "Expected CommandInitiatedNotification handler to be invoked");
        Assert.True(_cmdDoneNotif.Invoked, "Expected CommandCompletedNotification handler to be invoked");
    }

    [Fact(DisplayName = "Sync Query dispatches query, handler, and notifications")]
    public async Task SyncQuery_DispatchesCorrectly()
    {
        var dispatcher = BuildDispatcher(RunMode.Sync);
        var qry = new DummyQuery();

        var value = await dispatcher.ExecuteQuery(qry);

        Assert.Equal(42, value);
        Assert.True(_qryHandler.Handled, "Expected DummyQueryHandler.Handle(...) to be called");
        Assert.True(_qryInitNotif.Invoked, "Expected QueryInitiatedNotification handler to be invoked");
        Assert.True(_qryDoneNotif.Invoked, "Expected QueryCompletedNotification handler to be invoked");
        Assert.Equal(42, _qryDoneNotif.Result);
    }

    [Fact(DisplayName = "Async Command enqueues and executes in background, firing all handlers and notifications")]
    public async Task AsyncCommand_EnqueuesAndExecutes()
    {
        var dispatcher = BuildDispatcher(RunMode.Async);
        var cmd = new DummyCommand();

        // Fire-and-forget returns immediately, work enqueued
        var task = dispatcher.ExecuteCommand(cmd);
        Assert.False(task.IsCompleted, "Expected Task not completed before background execution");

        // Simulate background consumer
        var queuedTask = await _queue.DequeueAsync(CancellationToken.None);
        await queuedTask.WorkItem(CancellationToken.None);

        var result = await task;

        Assert.True(result.IsSuccess, "Expected CommandResult.IsSuccess == true after background execution");
        Assert.True(_cmdHandler.Handled, "Expected DummyCommandHandler.Handle(...) in background");
        Assert.True(_pre.Invoked, "Expected pre-handle attribute to run in background");
        Assert.True(_post.Invoked, "Expected post-handle attribute to run in background");
        Assert.True(_cmdInitNotif.Invoked, "Expected CommandInitiatedNotification in background");
        Assert.True(_cmdDoneNotif.Invoked, "Expected CommandCompletedNotification in background");
    }

    // No-op metrics reporter to satisfy BackgroundTaskQueue constructor
    private class NoOpMetricsReporter : IQueueMetricsReporter
    {
        public void ItemEnqueued()
        {
        }

        public void ItemDroppedNewest()
        {
        }

        public void ItemDroppedOldest()
        {
        }

        public long CurrentCount => 0;

        public void RecordLatency(TimeSpan latency)
        {
        }

        public void Dispose()
        {
        }
    }
}