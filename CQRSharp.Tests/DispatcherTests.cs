using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using CQRSharp.Core.Requests;
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
using CQRSharp.Shared.Data.Interfaces.Context;
using CQRSharp.Shared.Data.Interfaces.Handlers;
using CQRSharp.Shared.Data.Interfaces.Markers.Command;
using CQRSharp.Shared.Data.Interfaces.Markers.Query;
using CQRSharp.Shared.Data.Interfaces.Markers.Request;
using CQRSharp.Shared.Data.Models.Commands;
using CQRSharp.Shared.Data.Models.Requests;
using CQRSharp.Shared.Data.Attributes.Pipelines;
using CQRSharp.Shared.Data.Interfaces.Notifications;

namespace CQRSharp.Tests
{
    //Dummy command and handler
    public class DummyCommand : CommandBase { }
    public class DummyCommandHandler : ICommandHandler<DummyCommand>
    {
        public bool Handled { get; private set; }
        public Task<CommandResult> Handle(DummyCommand command, CancellationToken ct)
        {
            Handled = true;
            return Task.FromResult(CommandResult.FromSuccess());
        }
    }

    //Dummy query and handler
    public class DummyQuery : QueryBase<int> { }
    public class DummyQueryHandler : IQueryHandler<DummyQuery, int>
    {
        public bool Handled { get; private set; }
        public Task<int> Handle(DummyQuery query, CancellationToken ct)
        {
            Handled = true;
            return Task.FromResult(42);
        }
    }

    //Pre- and post-handler attributes
    internal class DummyPreHandler : IPreHandlerAttribute
    {
        public int PreHandlerExecutionPriority => 0;
        public bool Invoked { get; private set; }
        public Task OnBeforeHandle(IRequest req, IServiceProvider sp, CancellationToken ct)
        {
            Invoked = true;
            return Task.CompletedTask;
        }
    }

    internal class DummyPostHandler : IPostHandlerAttribute
    {
        public int PostHandlerExecutionPriority => 0;
        public bool Invoked { get; private set; }
        public Task OnAfterHandle(IRequest req, IServiceProvider sp, CancellationToken ct)
        {
            Invoked = true;
            return Task.CompletedTask;
        }
    }

    //Notification handlers
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

    public class DispatcherTests
    {
        private readonly DummyPreHandler    _pre            = new();
        private readonly DummyPostHandler   _post           = new();
        private readonly DummyCommandHandler _cmdHandler    = new();
        private readonly DummyQueryHandler   _qryHandler    = new();
        private readonly CommandInitiatedHandler _cmdInitNotif = new();
        private readonly CommandCompletedHandler _cmdDoneNotif = new();
        private readonly QueryInitiatedHandler   _qryInitNotif = new();
        private readonly QueryCompletedHandler   _qryDoneNotif = new();
        private readonly BackgroundTaskQueue    _queue;
        private readonly ServiceProvider        _syncProvider;

        public DispatcherTests()
        {
            //Build sync container + queue
            var services = new ServiceCollection();

            //Logging
            services.AddSingleton(LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.None)));
            services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));

            //Options
            services.AddSingleton(Options.Create(new DispatcherOptions { RunMode = RunMode.Sync }));
            services.AddSingleton(Options.Create(new BackgroundTaskQueueOptions()));

            //Background queue
            _queue = new BackgroundTaskQueue(services.BuildServiceProvider()
                .GetRequiredService<IOptions<BackgroundTaskQueueOptions>>());
            services.AddSingleton<IBackgroundTaskQueue>(_queue);

            //Request registry
            var reqMap = new ConcurrentDictionary<Type, RequestMetadata>
            {
                [typeof(DummyCommand)] = new RequestMetadata(
                    typeof(DummyCommand), typeof(DummyCommandHandler),
                    [_pre], [_post],
                    [],
                    [],
                    ResultType: null, ContextType: typeof(RequestContextBase)
                ),
                [typeof(DummyQuery)] = new RequestMetadata(
                    typeof(DummyQuery), typeof(DummyQueryHandler),
                    [],
                    [],
                    [],
                    [],
                    ResultType: typeof(int), ContextType: typeof(RequestContextBase)
                )
            };
            services.AddSingleton<IRequestRegistry>(new RequestRegistry(reqMap));

            //Handler registry
            var handlerMap = new ConcurrentDictionary<Type, HandlerInvokerDelegate>
            {
                [typeof(DummyCommand)] = (_,r,ct) =>
                    _cmdHandler.Handle((DummyCommand)r, ct)
                               .ContinueWith<object>(t => t.Result, ct),
                [typeof(DummyQuery)]   = (_,r,ct) =>
                    _qryHandler.Handle((DummyQuery)r, ct)
                               .ContinueWith<object>(t => t.Result, ct)
            };
            services.AddSingleton<IHandlerRegistry>(new HandlerRegistry(handlerMap));

            //Pipeline registry (empty)
            services.AddSingleton<IPipelineRegistry>(new PipelineRegistry(new ConcurrentDictionary<Type, PipelineBuilderDelegate>()));

            //Context factory
            services.AddSingleton<IContextFactoryRegistry>(
                new ContextFactoryRegistry(new ConcurrentDictionary<Type, Func<IServiceProvider, object>>
                {
                    [typeof(RequestContextBase)] = sp => sp.GetRequiredService<IRequestContextFactory>()
                }));
            services.AddTransient<IRequestContextFactory, DefaultRequestContextFactory>();

            //Notifications
            services.AddSingleton<INotificationDispatcher, NotificationDispatcher>();
            services.AddSingleton<INotificationHandler<CommandInitiatedNotification>>(_ => _cmdInitNotif);
            services.AddSingleton<INotificationHandler<CommandCompletedNotification>>(_ => _cmdDoneNotif);
            services.AddSingleton<INotificationHandler<QueryInitiatedNotification<int>>>(_ => _qryInitNotif);
            services.AddSingleton<INotificationHandler<QueryCompletedNotification<int>>>(_ => _qryDoneNotif);

            //Concrete handlers
            services.AddTransient<DummyCommandHandler>(_ => _cmdHandler);
            services.AddTransient<DummyQueryHandler>(_ => _qryHandler);

            //Dispatcher
            services.AddSingleton<IDispatcher, Dispatcher>();

            _syncProvider = services.BuildServiceProvider();
        }

        private IDispatcher BuildDispatcher(RunMode mode)
        {
            if (mode == RunMode.Sync)
                return _syncProvider.GetRequiredService<IDispatcher>();

            //Async container
            var asyncOpts = Options.Create(new DispatcherOptions { RunMode = RunMode.Async });
            var asyncServices = new ServiceCollection();

            asyncServices.AddSingleton(_ => asyncOpts);
            asyncServices.AddSingleton<IOptions<DispatcherOptions>>(_ => asyncOpts);
            asyncServices.AddSingleton<IOptions<BackgroundTaskQueueOptions>>(_ =>
                _syncProvider.GetRequiredService<IOptions<BackgroundTaskQueueOptions>>());

            asyncServices.AddSingleton(_syncProvider.GetRequiredService<ILoggerFactory>());
            asyncServices.AddSingleton(typeof(ILogger<>), typeof(Logger<>));

            asyncServices.AddSingleton<IBackgroundTaskQueue>(_queue);
            asyncServices.AddSingleton(_syncProvider.GetRequiredService<IRequestRegistry>());
            asyncServices.AddSingleton(_syncProvider.GetRequiredService<IHandlerRegistry>());
            asyncServices.AddSingleton(_syncProvider.GetRequiredService<IPipelineRegistry>());
            asyncServices.AddSingleton(_syncProvider.GetRequiredService<IContextFactoryRegistry>());
            asyncServices.AddTransient<IRequestContextFactory, DefaultRequestContextFactory>();

            asyncServices.AddSingleton<INotificationDispatcher, NotificationDispatcher>();
            asyncServices.AddSingleton<INotificationHandler<CommandInitiatedNotification>>(_ => _cmdInitNotif);
            asyncServices.AddSingleton<INotificationHandler<CommandCompletedNotification>>(_ => _cmdDoneNotif);
            asyncServices.AddSingleton<INotificationHandler<QueryInitiatedNotification<int>>>(_ => _qryInitNotif);
            asyncServices.AddSingleton<INotificationHandler<QueryCompletedNotification<int>>>(_ => _qryDoneNotif);

            asyncServices.AddSingleton<DummyCommandHandler>(_ => _cmdHandler);
            asyncServices.AddSingleton<IDispatcher, Dispatcher>();

            return asyncServices.BuildServiceProvider().GetRequiredService<IDispatcher>();
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

            //Fire-and-forget returns immediately, work enqueued
            var task = dispatcher.ExecuteCommand(cmd);
            Assert.False(task.IsCompleted, "Expected Task not completed before background execution");

            //Simulate background consumer
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
}