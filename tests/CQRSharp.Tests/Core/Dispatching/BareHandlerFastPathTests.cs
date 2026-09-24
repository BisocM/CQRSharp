using System.Runtime.CompilerServices;
using CQRSharp.Core.Pipelines;
using CQRSharp.Pipelines;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace CQRSharp.Tests.Core;

/// <summary>
///     The executor hands back the handler's own task when nothing brackets it (no interceptors, no lifecycle
///     subscribers, no behaviors, untraced, unmetered, outbox off). These pin that it does, for commands as for queries,
///     and that the shortcut is otherwise unobservable: results, failures and the null-result contract behave exactly as
///     on the fully bracketed path, and registering a subscriber turns the lifecycle notifications back on.
/// </summary>
public sealed class BareHandlerFastPathTests
{
    [Fact(DisplayName = "Fast path: with nothing around the handler, Send hands back the handler's own task")]
    public async Task Hands_back_the_handlers_own_task()
    {
        await using var provider = Build();
        await using var scope = provider.CreateAsyncScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>();
        var query = new FastQuery { Mode = FastMode.Value };
        var command = new FastCommand { Mode = FastMode.Value };

        var queryTask = dispatcher.Send(query, TestContext.Current.CancellationToken);
        var commandTask = dispatcher.Send(command, TestContext.Current.CancellationToken);

        queryTask.Should().BeSameAs(query.HandedBack);
        commandTask.Should().BeSameAs(command.HandedBack, "a command pays for no lifecycle it has no subscriber for");
        (await commandTask).IsSuccess.Should().BeTrue();
    }

    [Fact(DisplayName = "Fast path: the handler's value is returned")]
    public async Task Returns_the_handler_result()
    {
        await using var provider = Build();
        await using var scope = provider.CreateAsyncScope();

        var result = await scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>().Send(new FastQuery { Mode = FastMode.Value }, TestContext.Current.CancellationToken);

        result.Should().Be(new FastResult(7));
    }

    [Fact(DisplayName = "Fast path: a handler that throws before its first await faults the returned task, not the Send call")]
    public async Task Synchronous_throw_surfaces_through_the_task()
    {
        await using var provider = Build();
        await using var scope = provider.CreateAsyncScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>();

        Task<FastResult?>? task = null;
        Action send = () => task = dispatcher.Send(new FastQuery { Mode = FastMode.ThrowSynchronously });

        send.Should().NotThrow("Send must hand back a task even when the handler throws synchronously");
        (await FluentActions.Awaiting(() => task!).Should().ThrowAsync<InvalidOperationException>()).WithMessage("sync boom");
    }

    [Fact(DisplayName = "Fast path: an asynchronous failure propagates unchanged")]
    public async Task Asynchronous_throw_propagates()
    {
        await using var provider = Build();
        await using var scope = provider.CreateAsyncScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>();

        var act = () => dispatcher.Send(new FastQuery { Mode = FastMode.ThrowAfterAwait });

        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("async boom");
    }

    [Fact(DisplayName = "Fast path: a query may return null for a reference result")]
    public async Task Null_reference_result_is_allowed()
    {
        await using var provider = Build();
        await using var scope = provider.CreateAsyncScope();

        var result = await scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>().Send(new FastQuery { Mode = FastMode.Null }, TestContext.Current.CancellationToken);

        result.Should().BeNull();
    }

    [Fact(DisplayName = "Stream fast path: the items are the handler's, and the context is initialised before it runs")]
    public async Task Bare_stream_yields_the_handler_items()
    {
        await using var provider = Build();
        await using var scope = provider.CreateAsyncScope();
        var request = new FastStream { Count = 3 };

        var items = new List<FastItem>();
        await foreach (var item in scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>().Stream(request, TestContext.Current.CancellationToken))
            items.Add(item);

        items.Should().Equal(new FastItem(1), new FastItem(2), new FastItem(3));
        request.ContextAtHandle.Should().NotBeNull().And.BeSameAs(request.Context);
    }

    [Fact(DisplayName = "Stream fast path: dispatching never throws; a handler that fails up front faults the enumeration")]
    public async Task Bare_stream_failure_is_deferred_to_enumeration()
    {
        await using var provider = Build();
        await using var scope = provider.CreateAsyncScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>();

        IAsyncEnumerable<FastItem>? stream = null;
        Action dispatch = () => stream = dispatcher.Stream(new FastStream { Count = -1 });
        dispatch.Should().NotThrow();

        var enumerate = async () =>
        {
            await foreach (var _ in stream!)
            {
            }
        };
        (await enumerate.Should().ThrowAsync<ArgumentOutOfRangeException>()).WithMessage("*negative count*");
    }

    [Theory(DisplayName = "Stream fast path: the token given to Stream and the one the stream is enumerated with both reach the handler")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Bare_stream_observes_either_token(bool throughEnumeration)
    {
        await using var provider = Build();
        await using var scope = provider.CreateAsyncScope();
        using var dispatchToken = new CancellationTokenSource();
        using var enumerationToken = new CancellationTokenSource();

        var items = new List<FastItem>();
        var enumerate = async () =>
        {
            var stream = scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>().Stream(new FastWaitingStream(), dispatchToken.Token);
            await foreach (var item in stream.WithCancellation(enumerationToken.Token))
            {
                items.Add(item);
                await (throughEnumeration ? enumerationToken : dispatchToken).CancelAsync();
            }
        };

        await enumerate.Should().ThrowAsync<OperationCanceledException>();
        items.Should().Equal(new FastItem(1));
    }

    [Fact(DisplayName = "Fast path: a command's value is returned, and a failed result is returned as it is")]
    public async Task Command_result_is_returned()
    {
        await using var provider = Build();
        await using var scope = provider.CreateAsyncScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>();

        (await dispatcher.Send(new FastCommand { Mode = FastMode.Value }, TestContext.Current.CancellationToken)).IsSuccess.Should().BeTrue();
        (await dispatcher.Send(new FastCommand { Mode = FastMode.Failed }, TestContext.Current.CancellationToken))
            .Should().Be(CommandResult.Conflict("taken"));
    }

    [Fact(DisplayName = "Fast path: a command handler that throws before its first await faults the returned task, not the Send call")]
    public async Task Command_synchronous_throw_surfaces_through_the_task()
    {
        await using var provider = Build();
        await using var scope = provider.CreateAsyncScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>();

        Task<CommandResult>? task = null;
        Action send = () => task = dispatcher.Send(new FastCommand { Mode = FastMode.ThrowSynchronously });

        send.Should().NotThrow("Send must hand back a task even when the handler throws synchronously");
        (await FluentActions.Awaiting(() => task!).Should().ThrowAsync<InvalidOperationException>()).WithMessage("sync boom");
    }

    [Fact(DisplayName = "Fast path: a command handler that returns no CommandResult is rejected")]
    public async Task Command_null_result_is_rejected()
    {
        await using var provider = Build();
        await using var scope = provider.CreateAsyncScope();

        var act = () => scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>().Send(new FastCommand { Mode = FastMode.Null });

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned null*");
    }

    [Fact(DisplayName = "Registering a command lifecycle subscriber turns the command notifications back on")]
    public async Task Command_subscriber_re_enables_lifecycle_notifications()
    {
        var seen = new List<string>();
        await using var provider = Build(services =>
        {
            services.AddSingleton(seen);
            services.AddTransient<INotificationHandler<CommandInitiatedNotification>, FastCommandInitiatedSubscriber>();
            services.AddTransient<INotificationHandler<CommandCompletedNotification>, FastCommandCompletedSubscriber>();
        });
        await using var scope = provider.CreateAsyncScope();

        await scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>().Send(new FastCommand { Mode = FastMode.Value }, TestContext.Current.CancellationToken);

        seen.Should().Equal("initiated", "completed");
    }

    [Fact(DisplayName = "The request context is stamped from the application's TimeProvider, not the system clock")]
    public async Task Context_timestamp_follows_the_time_provider()
    {
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(new DateTimeOffset(2031, 5, 4, 3, 2, 1, TimeSpan.Zero));
        await using var provider = Build(services => services.AddSingleton<TimeProvider>(clock));
        await using var scope = provider.CreateAsyncScope();
        var query = new FastQuery { Mode = FastMode.Value };

        await scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>().Send(query, TestContext.Current.CancellationToken);

        query.Context!.CreatedAt.Should().Be(clock.GetUtcNow().UtcDateTime);
    }

    [Fact(DisplayName = "Registering a lifecycle subscriber turns the notifications back on for that request")]
    public async Task Subscriber_re_enables_lifecycle_notifications()
    {
        var seen = new List<string>();
        await using var provider = Build(services =>
        {
            services.AddSingleton(seen);
            services.AddTransient<INotificationHandler<QueryInitiatedNotification<FastResult?>>, FastInitiatedSubscriber>();
            services.AddTransient<INotificationHandler<QueryCompletedNotification<FastResult?>>, FastCompletedSubscriber>();
        });
        await using var scope = provider.CreateAsyncScope();

        await scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>().Send(new FastQuery { Mode = FastMode.Value }, TestContext.Current.CancellationToken);

        seen.Should().Equal("initiated", "completed");
    }

    // Private, so the generator does not auto-register them: an assembly-wide subscriber would switch the fast path off
    // for every other test here. The one test that wants them registers them by hand.
    private sealed class FastInitiatedSubscriber(List<string> seen) : INotificationHandler<QueryInitiatedNotification<FastResult?>>
    {
        public Task Handle(QueryInitiatedNotification<FastResult?> notification, CancellationToken cancellationToken)
        {
            seen.Add("initiated");
            return Task.CompletedTask;
        }
    }

    private sealed class FastCompletedSubscriber(List<string> seen) : INotificationHandler<QueryCompletedNotification<FastResult?>>
    {
        public Task Handle(QueryCompletedNotification<FastResult?> notification, CancellationToken cancellationToken)
        {
            seen.Add("completed");
            return Task.CompletedTask;
        }
    }

    private sealed class FastCommandInitiatedSubscriber(List<string> seen) : INotificationHandler<CommandInitiatedNotification>
    {
        public Task Handle(CommandInitiatedNotification notification, CancellationToken cancellationToken)
        {
            if (notification.Command is FastCommand) seen.Add("initiated");
            return Task.CompletedTask;
        }
    }

    private sealed class FastCommandCompletedSubscriber(List<string> seen) : INotificationHandler<CommandCompletedNotification>
    {
        public Task Handle(CommandCompletedNotification notification, CancellationToken cancellationToken)
        {
            if (notification.Command is FastCommand) seen.Add("completed");
            return Task.CompletedTask;
        }
    }

    // Without the default validation and exception-handling behaviors: nothing may bracket the handler.
    private static ServiceProvider Build(Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddCqrsGenerated(b => b.UseValidation(false).UseExceptionHandling(false));
        configure?.Invoke(services);
        return services.BuildServiceProvider();
    }
}

public enum FastMode
{
    Value,
    Failed,
    Null,
    ThrowSynchronously,
    ThrowAfterAwait
}

public sealed record FastResult(int Value);

// Its own result type, so no other fixture's QueryInitiated/Completed/Failed<T> subscriber ever applies to it.
public sealed class FastQuery : QueryBase<FastResult?>
{
    public FastMode Mode { get; init; }

    /// <summary>The task the handler returned, for a value.</summary>
    public Task<FastResult?>? HandedBack { get; set; }
}

public sealed class FastCommand : CommandBase
{
    public FastMode Mode { get; init; }

    /// <summary>The task the handler returned, for a value.</summary>
    public Task<CommandResult>? HandedBack { get; set; }
}

public sealed record FastItem(int Value);

// Its own item type, so no other fixture's Stream*Notification<T> subscriber or stream behavior applies to it.
public sealed class FastStream : StreamRequestBase<FastItem>
{
    public int Count { get; init; }

    /// <summary>The request's context when the dispatcher called the handler.</summary>
    public IRequestContext? ContextAtHandle { get; set; }
}

public sealed class FastStreamHandler : IStreamRequestHandler<FastStream, FastItem>
{
    // Not an iterator: it validates eagerly, so a bad request throws from Handle itself rather than from MoveNext.
    public IAsyncEnumerable<FastItem> Handle(FastStream request, CancellationToken cancellationToken)
    {
        request.ContextAtHandle = request.Context;
        if (request.Count < 0) throw new ArgumentOutOfRangeException(nameof(request), "negative count");
        return Produce(request.Count);

        static async IAsyncEnumerable<FastItem> Produce(int count)
        {
            for (var i = 1; i <= count; i++)
            {
                await Task.Yield();
                yield return new FastItem(i);
            }
        }
    }
}

public sealed class FastQueryHandler : IQueryHandler<FastQuery, FastResult?>
{
    public Task<FastResult?> Handle(FastQuery query, CancellationToken cancellationToken)
    {
        return query.Mode switch
        {
            FastMode.Value => query.HandedBack = Task.FromResult<FastResult?>(new FastResult(7)),
            FastMode.Null => Task.FromResult<FastResult?>(null),
            FastMode.ThrowSynchronously => throw new InvalidOperationException("sync boom"),
            _ => ThrowAfterAwait()
        };

        static async Task<FastResult?> ThrowAfterAwait()
        {
            await Task.Yield();
            throw new InvalidOperationException("async boom");
        }
    }
}

public sealed class FastCommandHandler : ICommandHandler<FastCommand>
{
    public Task<CommandResult> Handle(FastCommand command, CancellationToken cancellationToken)
    {
        return command.Mode switch
        {
            FastMode.Value => command.HandedBack = Task.FromResult(CommandResult.FromSuccess()),
            FastMode.Failed => Task.FromResult(CommandResult.Conflict("taken")),
            FastMode.Null => Task.FromResult<CommandResult>(null!),
            FastMode.ThrowSynchronously => throw new InvalidOperationException("sync boom"),
            _ => ThrowAfterAwait()
        };

        static async Task<CommandResult> ThrowAfterAwait()
        {
            await Task.Yield();
            throw new InvalidOperationException("async boom");
        }
    }
}

public sealed class FastWaitingStream : StreamRequestBase<FastItem>;

public sealed class FastWaitingStreamHandler : IStreamRequestHandler<FastWaitingStream, FastItem>
{
    public async IAsyncEnumerable<FastItem> Handle(FastWaitingStream request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return new FastItem(1);

        // Only a cancellation ends this wait.
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        yield return new FastItem(2);
    }
}
