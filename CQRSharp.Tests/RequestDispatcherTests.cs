using System.Collections.Concurrent;
using CQRSharp.Core.BackgroundTasks;
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
using CQRSharp.Shared.Data.Attributes.Pipelines;
using CQRSharp.Shared.Data.Interfaces.Context;
using CQRSharp.Shared.Data.Interfaces.Handlers;
using CQRSharp.Shared.Data.Interfaces.Markers.Command;
using CQRSharp.Shared.Data.Interfaces.Markers.Query;
using CQRSharp.Shared.Data.Interfaces.Markers.Request;
using CQRSharp.Shared.Data.Interfaces.Notifications;
using CQRSharp.Shared.Data.Models.Commands;
using CQRSharp.Shared.Data.Models.Requests;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CQRSharp.Tests;

// Dummy command and handler
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
internal class DummyPreHandler : IPreHandlerAttribute
{
    public bool Invoked { get; private set; }
    public int PreHandlerExecutionPriority => 0;

    public Task OnBeforeHandle(IRequest req, IServiceProvider sp, CancellationToken ct)
    {
        Invoked = true;
        return Task.CompletedTask;
    }
}

internal class DummyPostHandler : IPostHandlerAttribute
{
    public bool Invoked { get; private set; }
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

    public void StopApplication()
    {
        _cts.Cancel();
    }
}

public class RequestDispatcherTests
{
    private readonly CommandCompletedHandler _cmdDoneNotif = new();
    private readonly DummyCommandHandler _cmdHandler = new();
    private readonly CommandInitiatedHandler _cmdInitNotif = new();
    private readonly DummyPostHandler _post = new();
    private readonly DummyPreHandler _pre = new();
    private readonly QueryCompletedHandler _qryDoneNotif = new();
    private readonly DummyQueryHandler _qryHandler = new();
    private readonly QueryInitiatedHandler _qryInitNotif = new();
    private readonly IBackgroundTaskQueue _queue;
    private readonly ServiceProvider _syncProvider;

    public RequestDispatcherTests()
    {
        var services = new ServiceCollection();

        // Logging
        services.AddSingleton<ILoggerFactory>(_ => LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.None)));
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));

        // Host lifetime (needed for BackgroundTaskQueue constructor)
        services.AddSingleton<IHostApplicationLifetime, TestHostApplicationLifetime>();

        // Options
        services.AddSingleton<IOptions<DispatcherOptions>>(_ =>
            Options.Create(new DispatcherOptions { RunMode = RunMode.Sync }));
        services.AddSingleton<IOptions<BackgroundTaskQueueOptions>>(_ =>
            Options.Create(new BackgroundTaskQueueOptions()));

        // Notification dispatcher and handlers (also needed by queue)
        services.AddSingleton<INotificationDispatcher, NotificationDispatcher>();
        services.AddSingleton<INotificationHandler<CommandInitiatedNotification>>(_ => _cmdInitNotif);
        services.AddSingleton<INotificationHandler<CommandCompletedNotification>>(_ => _cmdDoneNotif);
        services.AddSingleton<INotificationHandler<QueryInitiatedNotification<int>>>(_ => _qryInitNotif);
        services.AddSingleton<INotificationHandler<QueryCompletedNotification<int>>>(_ => _qryDoneNotif);

        // Build an interim provider to resolve queue dependencies
        var interimProvider = services.BuildServiceProvider();
        _queue = new BackgroundTaskQueue(
            interimProvider.GetRequiredService<IOptions<BackgroundTaskQueueOptions>>(),
            interimProvider.GetRequiredService<INotificationDispatcher>(),
            interimProvider.GetRequiredService<IHostApplicationLifetime>(),
            interimProvider.GetRequiredService<ILogger<BackgroundTaskQueue>>()
        );
        services.AddSingleton(_queue);

        // Request registry
        var reqMap = new ConcurrentDictionary<Type, RequestMetadata>
        {
            [typeof(DummyCommand)] = new(
                typeof(DummyCommand),
                typeof(DummyCommandHandler),
                new[] { (IPreHandlerAttribute)_pre },
                new[] { (IPostHandlerAttribute)_post },
                [],
                [],
                null,
                typeof(RequestContextBase)
            ),
            [typeof(DummyQuery)] = new(
                typeof(DummyQuery),
                typeof(DummyQueryHandler),
                Array.Empty<IPreHandlerAttribute>(),
                Array.Empty<IPostHandlerAttribute>(),
                [],
                [],
                typeof(int),
                typeof(RequestContextBase)
            )
        };
        services.AddSingleton<IRequestRegistry>(new RequestRegistry(reqMap));

        // Handler registry
        var handlerMap = new ConcurrentDictionary<Type, HandlerInvokerDelegate>
        {
            [typeof(DummyCommand)] = (_, req, ct) =>
                _cmdHandler.Handle((DummyCommand)req, ct).ContinueWith<object>(t => t.Result, ct),
            [typeof(DummyQuery)] = (_, req, ct) =>
                _qryHandler.Handle((DummyQuery)req, ct).ContinueWith<object>(t => t.Result, ct)
        };
        services.AddSingleton<IHandlerRegistry>(new HandlerRegistry(handlerMap));

        // Pipeline registry (empty)
        services.AddSingleton<IPipelineRegistry>(
            new PipelineRegistry(new ConcurrentDictionary<Type, PipelineBuilderDelegate>()));

        // Context factory registry and factory
        services.AddSingleton<IContextFactoryRegistry>(new ContextFactoryRegistry(
            new ConcurrentDictionary<Type, Func<IServiceProvider, object>>
            {
                [typeof(RequestContextBase)] = sp => sp.GetRequiredService<IRequestContextFactory>()
            }));
        services.AddTransient<IRequestContextFactory, DefaultRequestContextFactory>();

        // Concrete handlers
        services.AddTransient<DummyCommandHandler>(_ => _cmdHandler);
        services.AddTransient<DummyQueryHandler>(_ => _qryHandler);

        // RequestDispatcher
        services.AddSingleton<IRequestDispatcher, RequestDispatcher>();

        // Build final service provider
        _syncProvider = services.BuildServiceProvider();
    }

    private IRequestDispatcher BuildDispatcher(RunMode mode)
    {
        if (mode == RunMode.Sync)
            return _syncProvider.GetRequiredService<IRequestDispatcher>();

        // Async container
        var asyncServices = new ServiceCollection();
        asyncServices.AddSingleton<IOptions<DispatcherOptions>>(_ =>
            Options.Create(new DispatcherOptions { RunMode = RunMode.Async }));
        asyncServices.AddSingleton(_syncProvider.GetRequiredService<IOptions<BackgroundTaskQueueOptions>>());
        asyncServices.AddSingleton(_syncProvider.GetRequiredService<ILoggerFactory>());
        asyncServices.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        asyncServices.AddSingleton(_syncProvider.GetRequiredService<IHostApplicationLifetime>());
        asyncServices.AddSingleton(_queue);
        asyncServices.AddSingleton(_syncProvider.GetRequiredService<IRequestRegistry>());
        asyncServices.AddSingleton(_syncProvider.GetRequiredService<IHandlerRegistry>());
        asyncServices.AddSingleton(_syncProvider.GetRequiredService<IPipelineRegistry>());
        asyncServices.AddSingleton(_syncProvider.GetRequiredService<IContextFactoryRegistry>());
        asyncServices.AddTransient<IRequestContextFactory, DefaultRequestContextFactory>();
        asyncServices.AddSingleton(_syncProvider.GetRequiredService<INotificationDispatcher>());
        asyncServices.AddSingleton<INotificationHandler<CommandInitiatedNotification>>(_ => _cmdInitNotif);
        asyncServices.AddSingleton<INotificationHandler<CommandCompletedNotification>>(_ => _cmdDoneNotif);
        asyncServices.AddSingleton<INotificationHandler<QueryInitiatedNotification<int>>>(_ => _qryInitNotif);
        asyncServices.AddSingleton<INotificationHandler<QueryCompletedNotification<int>>>(_ => _qryDoneNotif);
        asyncServices.AddTransient<DummyCommandHandler>(_ => _cmdHandler);
        asyncServices.AddSingleton<IRequestDispatcher, RequestDispatcher>();

        var asyncProvider = asyncServices.BuildServiceProvider();
        return asyncProvider.GetRequiredService<IRequestDispatcher>();
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
    public async Task AsyncCommand_FireAndForget()
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
}