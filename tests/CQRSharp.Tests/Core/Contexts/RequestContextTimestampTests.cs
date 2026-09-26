using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;

namespace CQRSharp.Tests.Core;

/// <summary>
///     What <see cref="IRequestContext.CreatedAt" /> means: the time the request was sent, read from the application's
///     <see cref="TimeProvider" /> when the dispatcher builds its context, never the time it is first read. The handlers
///     here move the fake clock on before they read it, so a context that took its time at the read would report the
///     later time. The rule holds for the built-in context and for a custom one built without a time, inline and on the
///     background queue, where the context is built on the caller's flow and read on the consumer's; a custom context
///     built with an explicit time keeps it.
/// </summary>
public sealed class RequestContextTimestampTests
{
    private static readonly DateTimeOffset SentAt = new(2031, 7, 8, 9, 10, 11, TimeSpan.Zero);

    [Fact(DisplayName = "The built-in context's CreatedAt is the time the request was sent, not the time the handler reads it")]
    public async Task Default_context_is_stamped_when_sent()
    {
        var clock = new FakeTimeProvider(SentAt);
        await using var provider = Build(clock);
        await using var scope = provider.CreateAsyncScope();

        var seen = await scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>()
            .Send(new DefaultTimestampProbeQuery(), TestContext.Current.CancellationToken);

        seen.ReadAt.Should().Be(SentAt.UtcDateTime.AddHours(1), "the handler read the context an hour after it was sent");
        seen.CreatedAt.Should().Be(SentAt.UtcDateTime);
        seen.CreatedAt.Kind.Should().Be(DateTimeKind.Utc);
    }

    [Fact(DisplayName = "A custom context built without a time takes the time the request was sent, not the time the handler reads it")]
    public async Task Parameterless_custom_context_is_stamped_when_sent()
    {
        var clock = new FakeTimeProvider(SentAt);
        await using var provider = Build(clock);
        await using var scope = provider.CreateAsyncScope();

        var seen = await scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>()
            .Send(new StampedTimestampProbeQuery(), TestContext.Current.CancellationToken);

        seen.ReadAt.Should().Be(SentAt.UtcDateTime.AddHours(1));
        seen.CreatedAt.Should().Be(SentAt.UtcDateTime);
        seen.CreatedAt.Kind.Should().Be(DateTimeKind.Utc);
    }

    [Fact(DisplayName = "A custom context built with an explicit time keeps it: the dispatcher does not stamp it")]
    public async Task Explicitly_timed_custom_context_keeps_its_time()
    {
        var clock = new FakeTimeProvider(SentAt);
        await using var provider = Build(clock);
        await using var scope = provider.CreateAsyncScope();

        var seen = await scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>()
            .Send(new ExplicitTimestampProbeQuery(), TestContext.Current.CancellationToken);

        seen.CreatedAt.Should().Be(ExplicitTimeContextFactory.Time);
        seen.CreatedAt.Kind.Should().Be(DateTimeKind.Utc);
    }

    [Theory(DisplayName = "Under RunMode.Queued a context is stamped on the caller's flow when the request is sent, and read unchanged on the consumer's")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Queued_request_context_is_stamped_when_sent(bool customContext)
    {
        var clock = new FakeTimeProvider(SentAt);
        using var host = new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton<TimeProvider>(clock);
                services.AddCqrsGenerated(b => b
                    .ConfigureDispatcher(o => o.RunMode = RunMode.Queued)
                    .ConfigureQueue(o => o.ConsumerCount = 1));
            })
            .Build();
        await host.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await using var scope = host.Services.CreateAsyncScope();
            var cqrs = scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>();

            var seen = customContext
                ? await cqrs.Send(new StampedTimestampProbeQuery(), TestContext.Current.CancellationToken)
                : await cqrs.Send(new DefaultTimestampProbeQuery(), TestContext.Current.CancellationToken);

            seen.ReadAt.Should().Be(SentAt.UtcDateTime.AddHours(1));
            seen.CreatedAt.Should().Be(SentAt.UtcDateTime);
        }
        finally
        {
            await host.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact(DisplayName = "A context a factory builds while the dispatcher calls it reads the application's clock, and that reading is its stamp")]
    public async Task A_context_built_in_the_factory_reads_the_application_clock()
    {
        var clock = new FakeTimeProvider(SentAt);
        await using var provider = Build(clock);
        await using var scope = provider.CreateAsyncScope();
        var query = new ObservedTimestampProbeQuery();

        var seen = await scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>().Send(query, TestContext.Current.CancellationToken);

        query.Context!.CreatedAtWhenBuilt.Should().Be(SentAt.UtcDateTime, "the factory's context read the application's clock, not the system's");
        seen.CreatedAt.Should().Be(SentAt.UtcDateTime);
        seen.CreatedAt.Kind.Should().Be(DateTimeKind.Utc);
    }

    [Fact(DisplayName = "A context a factory builds before it awaits is stamped when the factory completes, as before")]
    public async Task A_context_built_before_an_await_is_stamped_when_the_factory_completes()
    {
        var clock = new FakeTimeProvider(SentAt);
        await using var provider = Build(clock);
        await using var scope = provider.CreateAsyncScope();
        var hydration = new TaskCompletionSource();
        var query = new ObservedTimestampProbeQuery { Hydration = hydration.Task };

        var sent = scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>().Send(query, TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromMinutes(5));
        hydration.SetResult();
        await sent;

        query.Context!.CreatedAtWhenBuilt.Should().Be(SentAt.UtcDateTime);
        query.Context.CreatedAt.Should().Be(SentAt.UtcDateTime.AddMinutes(5), "an asynchronously hydrated context is stamped once it is ready");
    }

    [Fact(DisplayName = "Outside the dispatcher's call of a factory, the parameterless constructor reads the system clock again, even after a factory threw")]
    public async Task The_application_clock_is_lent_only_during_the_factory_call()
    {
        var clock = new FakeTimeProvider(SentAt);
        await using var provider = Build(clock);
        await using var scope = provider.CreateAsyncScope();
        var cqrs = scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>();

        // Checked on the thread that sent, right after the dispatcher returned from the factory, before any await.
        var sent = cqrs.Send(new ObservedTimestampProbeQuery(), TestContext.Current.CancellationToken);
        new RequestContextBase().IsTimestampPending.Should().BeTrue("a context built outside a dispatch is provisional");
        await sent;

        var failed = cqrs.Send(new ObservedTimestampProbeQuery { FailInFactory = true }, TestContext.Current.CancellationToken);
        new RequestContextBase().IsTimestampPending.Should().BeTrue("a factory that threw leaves nothing lent behind");
        await failed.Invoking(t => t).Should().ThrowAsync<InvalidOperationException>().WithMessage("factory failed");
    }

    private static ServiceProvider Build(TimeProvider clock)
    {
        var services = new ServiceCollection();
        services.AddSingleton(clock);
        services.AddCqrsGenerated();
        return services.BuildServiceProvider();
    }
}

/// <summary>When a handler read its request's context, and what the context said.</summary>
public sealed record TimestampSeen(DateTime CreatedAt, DateTime ReadAt);

/// <summary>A query with the built-in context.</summary>
public sealed class DefaultTimestampProbeQuery : QueryBase<TimestampSeen>;

/// <summary>A query whose custom context is built with the parameterless constructor (<see cref="StampedContextFactory" />).</summary>
public sealed class StampedTimestampProbeQuery : QueryBase<TimestampSeen, StampedContext>;

/// <summary>A query whose custom context is built with an explicit time (<see cref="ExplicitTimeContextFactory" />).</summary>
public sealed class ExplicitTimestampProbeQuery : QueryBase<TimestampSeen, ExplicitTimeContext>;

public sealed class ExplicitTimeContext(DateTime createdAt) : RequestContextBase(createdAt);

public sealed class ExplicitTimeContextFactory : IRequestContextFactory<ExplicitTimeContext>
{
    public static readonly DateTime Time = new(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc);

    public ValueTask<ExplicitTimeContext> CreateContextAsync(IRequest request, CancellationToken cancellationToken) => new(new ExplicitTimeContext(Time));
}

/// <summary>A custom context that remembers what its CreatedAt was when its factory built it.</summary>
public sealed class ObservedTimeContext : RequestContextBase
{
    public ObservedTimeContext() => CreatedAtWhenBuilt = CreatedAt;

    public DateTime CreatedAtWhenBuilt { get; }
}

/// <summary>A query whose context factory builds the context first, then waits for <see cref="Hydration" /> when given one.</summary>
public sealed class ObservedTimestampProbeQuery : QueryBase<TimestampSeen, ObservedTimeContext>
{
    public Task? Hydration { get; init; }
    public bool FailInFactory { get; init; }
}

public sealed class ObservedTimeContextFactory : IRequestContextFactory<ObservedTimeContext>
{
    public ValueTask<ObservedTimeContext> CreateContextAsync(IRequest request, CancellationToken cancellationToken)
    {
        var context = new ObservedTimeContext();
        return request switch
        {
            ObservedTimestampProbeQuery { FailInFactory: true } => throw new InvalidOperationException("factory failed"),
            ObservedTimestampProbeQuery { Hydration: { } hydration } => AfterHydration(context, hydration),
            _ => new ValueTask<ObservedTimeContext>(context)
        };

        static async ValueTask<ObservedTimeContext> AfterHydration(ObservedTimeContext context, Task hydration)
        {
            await hydration;
            return context;
        }
    }
}

public sealed class ObservedTimestampProbeQueryHandler(TimeProvider clock) : IQueryHandler<ObservedTimestampProbeQuery, TimestampSeen>
{
    public Task<TimestampSeen> Handle(ObservedTimestampProbeQuery query, CancellationToken cancellationToken)
        => Task.FromResult(new TimestampSeen(query.Context!.CreatedAt, clock.GetUtcNow().UtcDateTime));
}

// Each handler moves a fake clock on by an hour before it reads the context, so a context that took its time at the read
// would report the later time. Under the system clock (every other test's container) it only reads.
public sealed class DefaultTimestampProbeQueryHandler(TimeProvider clock) : IQueryHandler<DefaultTimestampProbeQuery, TimestampSeen>
{
    public Task<TimestampSeen> Handle(DefaultTimestampProbeQuery query, CancellationToken cancellationToken)
        => Task.FromResult(TimestampProbe.Read(clock, query.Context!));
}

public sealed class StampedTimestampProbeQueryHandler(TimeProvider clock) : IQueryHandler<StampedTimestampProbeQuery, TimestampSeen>
{
    public Task<TimestampSeen> Handle(StampedTimestampProbeQuery query, CancellationToken cancellationToken)
        => Task.FromResult(TimestampProbe.Read(clock, query.Context!));
}

public sealed class ExplicitTimestampProbeQueryHandler(TimeProvider clock) : IQueryHandler<ExplicitTimestampProbeQuery, TimestampSeen>
{
    public Task<TimestampSeen> Handle(ExplicitTimestampProbeQuery query, CancellationToken cancellationToken)
        => Task.FromResult(TimestampProbe.Read(clock, query.Context!));
}

internal static class TimestampProbe
{
    public static TimestampSeen Read(TimeProvider clock, IRequestContext context)
    {
        (clock as FakeTimeProvider)?.Advance(TimeSpan.FromHours(1));
        return new TimestampSeen(context.CreatedAt, clock.GetUtcNow().UtcDateTime);
    }
}
