using CQRSharp.Pipelines;
using CQRSharp.Core.Pipelines;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace CQRSharp.Tests.Core;

/// <summary>
///     The 5.0 executor returns the handler's own task when nothing brackets it (no interceptors, no lifecycle
///     subscribers, no behaviors, outbox off). These pin that the shortcut is unobservable: results, failures and the
///     null-result contract behave exactly as on the fully bracketed path, and registering a subscriber turns the
///     lifecycle notifications back on.
/// </summary>
public sealed class BareHandlerFastPathTests
{
    [Fact(DisplayName = "Fast path: the handler's value is returned")]
    public async Task Returns_the_handler_result()
    {
        await using var provider = Build();
        await using var scope = provider.CreateAsyncScope();

        var result = await scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>().Send(new FastQuery { Mode = FastMode.Value });

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

        var result = await scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>().Send(new FastQuery { Mode = FastMode.Null });

        result.Should().BeNull();
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

        await scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>().Send(new FastQuery { Mode = FastMode.Value });

        seen.Should().Equal("initiated", "completed");
    }

    [Fact(DisplayName = "A replaced IRequestDispatcher is honoured: the direct-construction shortcut only applies to the built-in wiring")]
    public async Task Replaced_request_dispatcher_is_used()
    {
        var calls = new List<string>();
        await using var provider = Build(services =>
        {
            services.AddSingleton(calls);
            services.AddTransient<IRequestDispatcher, RecordingRequestDispatcher>();
        });
        await using var scope = provider.CreateAsyncScope();

        var act = () => scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>().Send(new FastQuery { Mode = FastMode.Value });

        await act.Should().ThrowAsync<NotSupportedException>();
        calls.Should().Equal(nameof(FastQuery));
    }

    private sealed class RecordingRequestDispatcher(List<string> calls) : IRequestDispatcher
    {
        public Task<TResponse> ExecuteAsync<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            calls.Add(request.GetType().Name);
            throw new NotSupportedException("custom dispatcher reached");
        }

        public Task<object?> ExecuteAsync(IRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
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

    private static ServiceProvider Build(Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddCqrsGenerated();
        configure?.Invoke(services);
        return services.BuildServiceProvider();
    }
}

public enum FastMode
{
    Value,
    Null,
    ThrowSynchronously,
    ThrowAfterAwait
}

public sealed record FastResult(int Value);

// Its own result type, so no other fixture's QueryInitiated/Completed/Failed<T> subscriber ever applies to it.
public sealed class FastQuery : QueryBase<FastResult?>
{
    public FastMode Mode { get; init; }
}

public sealed class FastQueryHandler : IQueryHandler<FastQuery, FastResult?>
{
    public Task<FastResult?> Handle(FastQuery query, CancellationToken cancellationToken)
    {
        return query.Mode switch
        {
            FastMode.Value => Task.FromResult<FastResult?>(new FastResult(7)),
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
