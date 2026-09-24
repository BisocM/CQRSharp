using System.Runtime.CompilerServices;
using CQRSharp.Core.BackgroundTasks;
using CQRSharp.Tests.Shared;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace CQRSharp.Tests.Core;

/// <summary>
///     A request's context is its caller's, whatever <see cref="RunMode" /> and <see cref="ExecutionScopeMode" /> say:
///     the context factory is resolved from the scope of the dispatcher the request is sent through and runs on the flow
///     that sends it, before the request is handed to the queue or to a scope of its own. So a factory that reads the
///     caller from a scoped service (<see cref="CallerIdentity" />) or from ambient state (<see cref="AmbientIdentity" />, as
///     <c>IHttpContextAccessor</c> keeps the current HTTP user) sees the caller, synchronously or after hydrating
///     asynchronously, while the handler runs in another scope or on the queue's consumer.
/// </summary>
public sealed class CallerContextTests
{
    private const string ScopedCaller = "ada (scope)";
    private const string AmbientCallerName = "ada (ambient)";

    [Theory(DisplayName = "A sent request's context is built from its caller's scope and flow, wherever the request runs")]
    [InlineData(RunMode.Inline, ExecutionScopeMode.Current, false)]
    [InlineData(RunMode.Inline, ExecutionScopeMode.Current, true)]
    [InlineData(RunMode.Inline, ExecutionScopeMode.New, false)]
    [InlineData(RunMode.Inline, ExecutionScopeMode.New, true)]
    [InlineData(RunMode.Queued, ExecutionScopeMode.Current, false)]
    [InlineData(RunMode.Queued, ExecutionScopeMode.Current, true)]
    [InlineData(RunMode.Queued, ExecutionScopeMode.New, false)]
    [InlineData(RunMode.Queued, ExecutionScopeMode.New, true)]
    public async Task Sent_request_context_is_the_callers(RunMode runMode, ExecutionScopeMode scopeMode, bool hydratesAsynchronously)
    {
        using var host = BuildHost(runMode, scopeMode);
        // Started before the caller's ambient state is set, so the queue's consumer does not inherit it.
        await host.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await using var scope = host.Services.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<CallerIdentity>().Name = ScopedCaller;
            AmbientIdentity.Name = AmbientCallerName;
            var hydration = hydratesAsynchronously ? new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) : null;
            var query = new CallerProbeQuery { Hydration = hydration?.Task };

            var sent = scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>().Send(query, TestContext.Current.CancellationToken);
            hydration?.SetResult();
            var seen = await sent.WaitAsync(TestContext.Current.CancellationToken);

            seen.ScopedCaller.Should().Be(ScopedCaller);
            seen.AmbientCaller.Should().Be(AmbientCallerName);

            // Where the handler ran, to show the context did not simply come from there.
            var inCallerScope = seen.ScopeId == scope.ServiceProvider.GetRequiredService<ScopedMarker>().Id;
            inCallerScope.Should().Be(runMode == RunMode.Inline && scopeMode == ExecutionScopeMode.Current);
            seen.HandlerAmbient.Should().Be(runMode == RunMode.Inline ? AmbientCallerName : null,
                "a queued handler runs on the queue's consumer, not on its caller's flow");
        }
        finally
        {
            await host.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Theory(DisplayName = "A stream's context is built from its caller's scope and flow, in the scope ScopeMode gives the stream")]
    [InlineData(RunMode.Inline, ExecutionScopeMode.Current, false)]
    [InlineData(RunMode.Inline, ExecutionScopeMode.Current, true)]
    [InlineData(RunMode.Inline, ExecutionScopeMode.New, false)]
    [InlineData(RunMode.Inline, ExecutionScopeMode.New, true)]
    [InlineData(RunMode.Queued, ExecutionScopeMode.Current, false)]
    [InlineData(RunMode.Queued, ExecutionScopeMode.Current, true)]
    [InlineData(RunMode.Queued, ExecutionScopeMode.New, false)]
    [InlineData(RunMode.Queued, ExecutionScopeMode.New, true)]
    public async Task Stream_context_is_the_callers(RunMode runMode, ExecutionScopeMode scopeMode, bool hydratesAsynchronously)
    {
        using var host = BuildHost(runMode, scopeMode);
        await host.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await using var scope = host.Services.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<CallerIdentity>().Name = ScopedCaller;
            AmbientIdentity.Name = AmbientCallerName;
            var hydration = hydratesAsynchronously ? new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) : null;

            await using var items = scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>()
                .Stream(new CallerProbeStream { Hydration = hydration?.Task }, TestContext.Current.CancellationToken)
                .GetAsyncEnumerator(TestContext.Current.CancellationToken);
            var first = items.MoveNextAsync();
            hydration?.SetResult();
            (await first).Should().BeTrue();
            var seen = items.Current;
            (await items.MoveNextAsync()).Should().BeFalse();

            seen.ScopedCaller.Should().Be(ScopedCaller);
            seen.AmbientCaller.Should().Be(AmbientCallerName);
            (seen.ScopeId == scope.ServiceProvider.GetRequiredService<ScopedMarker>().Id).Should().Be(scopeMode == ExecutionScopeMode.Current);
            seen.HandlerAmbient.Should().Be(AmbientCallerName, "a stream runs on the flow that enumerates it in every run mode");
        }
        finally
        {
            await host.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    // A request a handler sends is sent by that handler: its context comes from the handler's scope, not from the scope
    // the nested request is given (a queued handler's nested request runs at once, in a scope of its own).
    [Theory(DisplayName = "A request a handler sends in a scope of its own takes its context from that handler's scope")]
    [InlineData(RunMode.Inline, ExecutionScopeMode.New)]
    [InlineData(RunMode.Queued, ExecutionScopeMode.Current)]
    [InlineData(RunMode.Queued, ExecutionScopeMode.New)]
    public async Task Nested_request_context_is_the_sending_handlers(RunMode runMode, ExecutionScopeMode scopeMode)
    {
        using var host = BuildHost(runMode, scopeMode);
        await host.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await using var scope = host.Services.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<CallerIdentity>().Name = ScopedCaller;

            var seen = await scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>()
                .Send(new RelayCallerProbeQuery(), TestContext.Current.CancellationToken)
                .WaitAsync(TestContext.Current.CancellationToken);

            seen.ScopedCaller.Should().Be(RelayCallerProbeQueryHandler.Relay);
        }
        finally
        {
            await host.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact(DisplayName = "Queued: the context is captured before the request is queued, and a caller that gives up while it waits still withdraws it")]
    public async Task Giving_up_on_a_waiting_request_withdraws_it()
    {
        using var host = BuildHost(RunMode.Queued, ExecutionScopeMode.Current);
        await host.StartAsync(TestContext.Current.CancellationToken);
        var gate = host.Services.GetRequiredService<QueueGate>();
        try
        {
            await using var scope = host.Services.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<CallerIdentity>().Name = ScopedCaller;
            var cqrs = scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>();
            var blocker = cqrs.Send(new BlockingQueuedCommand(), TestContext.Current.CancellationToken);
            await gate.Running.Task.WaitAsync(TestContext.Current.CancellationToken);

            // The context is built as Send is called, so the request is already queued, behind the blocker, once Send
            // returns.
            using var giveUp = new CancellationTokenSource();
            var query = new CallerProbeQuery();
            var waiting = cqrs.Send(query, giveUp.Token);
            query.Context.Should().NotBeNull("the context is captured before the request is queued");
            await giveUp.CancelAsync();

            var cancelled = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
            cancelled.CancellationToken.Should().Be(giveUp.Token);
            blocker.IsCompleted.Should().BeFalse("the caller was answered while the request ahead still held the only slot");

            gate.Release.SetResult();
            (await blocker.WaitAsync(TestContext.Current.CancellationToken)).IsSuccess.Should().BeTrue();

            // One consumer takes the queue in order: once a request queued after the withdrawn one has run, the withdrawn
            // one has been passed over.
            await cqrs.Send(new CallerProbeQuery(), TestContext.Current.CancellationToken).WaitAsync(TestContext.Current.CancellationToken);
            query.Handled.Should().BeFalse();
        }
        finally
        {
            gate.Release.TrySetResult();
            await host.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact(DisplayName = "Queued: a caller that gives up while its context hydrates is answered with its cancellation, and nothing runs")]
    public async Task Giving_up_while_the_context_hydrates_cancels_the_dispatch()
    {
        using var host = BuildHost(RunMode.Queued, ExecutionScopeMode.Current);
        await host.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await using var scope = host.Services.CreateAsyncScope();
            var cqrs = scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>();
            using var giveUp = new CancellationTokenSource();
            var neverHydrates = new TaskCompletionSource();
            var query = new CallerProbeQuery { Hydration = neverHydrates.Task };

            var sending = cqrs.Send(query, giveUp.Token);
            await giveUp.CancelAsync();

            var cancelled = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sending);
            cancelled.CancellationToken.Should().Be(giveUp.Token);
            query.Context.Should().BeNull();

            await cqrs.Send(new CallerProbeQuery(), TestContext.Current.CancellationToken).WaitAsync(TestContext.Current.CancellationToken);
            query.Handled.Should().BeFalse();
        }
        finally
        {
            await host.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    // What a queued handler's request fired and forgotten meets when it is sent after the handler's scope is gone: the
    // scope its caller was read from no longer exists. Running it with another scope's (empty) caller instead would
    // attribute the request to nobody without saying so.
    [Fact(DisplayName = "Queued: a request sent through a dispatcher whose scope has ended fails instead of running without its caller")]
    public async Task A_dispatcher_whose_scope_ended_cannot_capture_its_caller()
    {
        using var host = BuildHost(RunMode.Queued, ExecutionScopeMode.Current);
        await host.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            ICqrsDispatcher orphaned;
            await using (var scope = host.Services.CreateAsyncScope())
                orphaned = scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>();

            // Sent from queued work, as a queued handler's request is: it runs at once, in a scope of its own.
            var query = new CallerProbeQuery();
            var sending = host.Services.GetRequiredService<IBackgroundTaskManager>()
                .EnqueueAsync(ct => orphaned.Send(query, ct), TestContext.Current.CancellationToken);

            await Assert.ThrowsAsync<ObjectDisposedException>(() => sending.WaitAsync(TestContext.Current.CancellationToken));
            query.Handled.Should().BeFalse();
        }
        finally
        {
            await host.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    private static IHost BuildHost(RunMode runMode, ExecutionScopeMode scopeMode)
        => new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddScoped<CallerIdentity>();
                services.AddScoped<ScopedMarker>();
                services.AddSingleton<QueueGate>();
                services.AddCqrsGenerated(b => b
                    .ConfigureDispatcher(o =>
                    {
                        o.RunMode = runMode;
                        o.ScopeMode = scopeMode;
                    })
                    .ConfigureQueue(o => o.ConsumerCount = 1));
            })
            .Build();
}

/// <summary>Who a DI scope acts for, as a web host fills it from the authenticated principal.</summary>
public sealed class CallerIdentity
{
    public string Name { get; set; } = "";
}

/// <summary>Ambient caller state that flows with the asynchronous flow, as <c>IHttpContextAccessor</c>'s HttpContext does.</summary>
public static class AmbientIdentity
{
    private static readonly AsyncLocal<string?> Current = new();

    public static string? Name
    {
        get => Current.Value;
        set => Current.Value = value;
    }
}

/// <summary>A request whose context the <see cref="CallerContextFactory" /> hydrates once <see cref="Hydration" /> completes.</summary>
public interface ICallerProbe
{
    Task? Hydration { get; }
}

public sealed class CallerContext : RequestContextBase
{
    public required string ScopedCaller { get; init; }
    public required string? AmbientCaller { get; init; }
}

// The identity is an optional dependency: every other test in this assembly composes the application without a
// CallerIdentity, and startup validation there still resolves every context factory.
public sealed class CallerContextFactory(CallerIdentity? identity = null) : IRequestContextFactory<CallerContext>
{
    public async ValueTask<CallerContext> CreateContextAsync(IRequest request, CancellationToken cancellationToken)
    {
        if (request is ICallerProbe { Hydration: { } hydration })
            await hydration.WaitAsync(cancellationToken);

        return new CallerContext { ScopedCaller = identity?.Name ?? "", AmbientCaller = AmbientIdentity.Name };
    }
}

/// <summary>What a probe's handler saw: its context, the scope it ran in, and the ambient caller on its own flow.</summary>
public sealed record CallerSeen(string ScopedCaller, string? AmbientCaller, Guid ScopeId, string? HandlerAmbient)
{
    public static CallerSeen Of(CallerContext context, ScopedMarker marker)
        => new(context.ScopedCaller, context.AmbientCaller, marker.Id, AmbientIdentity.Name);
}

public sealed class CallerProbeQuery : QueryBase<CallerSeen, CallerContext>, ICallerProbe
{
    public Task? Hydration { get; init; }
    public bool Handled { get; set; }
}

public sealed class CallerProbeQueryHandler(ScopedMarker marker) : IQueryHandler<CallerProbeQuery, CallerSeen>
{
    public Task<CallerSeen> Handle(CallerProbeQuery query, CancellationToken cancellationToken)
    {
        query.Handled = true;
        return Task.FromResult(CallerSeen.Of(query.Context!, marker));
    }
}

public sealed class CallerProbeStream : StreamRequestBase<CallerSeen, CallerContext>, ICallerProbe
{
    public Task? Hydration { get; init; }
}

public sealed class CallerProbeStreamHandler(ScopedMarker marker) : IStreamRequestHandler<CallerProbeStream, CallerSeen>
{
    public async IAsyncEnumerable<CallerSeen> Handle(CallerProbeStream request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        yield return CallerSeen.Of(request.Context!, marker);
    }
}

/// <summary>Acts for a caller of its own in the scope its handler runs in, and sends a <see cref="CallerProbeQuery" /> from there.</summary>
public sealed class RelayCallerProbeQuery : QueryBase<CallerSeen>;

public sealed class RelayCallerProbeQueryHandler(CallerIdentity identity, ICqrsDispatcher dispatcher) : IQueryHandler<RelayCallerProbeQuery, CallerSeen>
{
    public const string Relay = "relay";

    public Task<CallerSeen> Handle(RelayCallerProbeQuery query, CancellationToken cancellationToken)
    {
        identity.Name = Relay;
        return dispatcher.Send(new CallerProbeQuery(), cancellationToken);
    }
}
