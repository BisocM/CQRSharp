using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace CQRSharp.Tests.Core;

/// <summary>
///     The context-factory contract: <see cref="IRequestContextFactory{TContext}" /> is the one contract a factory
///     implements, the dispatcher calls it (or builds the built-in default context itself) before the handler runs, and a
///     factory registered by hand for the default context, before or after <c>AddCqrsGenerated</c>, replaces the
///     built-in one exactly as a factory a referenced library declares does. A factory that completes asynchronously is
///     <see cref="AsyncContextHydrationTests" />' subject.
/// </summary>
public sealed class RequestContextFactoryTests
{
    private static readonly DateTimeOffset Start = new(2030, 1, 2, 3, 4, 5, TimeSpan.Zero);

    [Fact(DisplayName = "The built-in factory stamps each context with the injected clock's time, read at creation")]
    public async Task Default_factory_uses_the_injected_clock()
    {
        var clock = new FakeTimeProvider(Start);
        var factory = new DefaultRequestContextFactory(clock);

        var first = await factory.CreateContextAsync(new DefaultContextProbeQuery(), TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromMinutes(1));
        var second = await factory.CreateContextAsync(new DefaultContextProbeQuery(), TestContext.Current.CancellationToken);

        first.Should().BeOfType<RequestContextBase>();
        first.CreatedAt.Should().Be(Start.UtcDateTime);
        first.CreatedAt.Kind.Should().Be(DateTimeKind.Utc);
        second.CreatedAt.Should().Be(Start.UtcDateTime.AddMinutes(1));
    }

    [Fact(DisplayName = "The container's IRequestContextFactory<RequestContextBase> is the built-in factory, on the registered clock")]
    public async Task Default_factory_is_registered_for_the_default_context()
    {
        var clock = new FakeTimeProvider(Start);
        await using var provider = Build(services => services.AddSingleton<TimeProvider>(clock));
        await using var scope = provider.CreateAsyncScope();

        var factory = scope.ServiceProvider.GetRequiredService<IRequestContextFactory<RequestContextBase>>();
        var context = await factory.CreateContextAsync(new DefaultContextProbeQuery(), TestContext.Current.CancellationToken);

        factory.Should().BeOfType<DefaultRequestContextFactory>();
        context.CreatedAt.Should().Be(Start.UtcDateTime);
    }

    [Fact(DisplayName = "A custom context built without a timestamp is stamped from the application's clock")]
    public async Task Parameterless_custom_context_takes_the_application_clock()
    {
        var clock = new FakeTimeProvider(Start);
        await using var provider = Build(services => services.AddSingleton<TimeProvider>(clock));
        await using var scope = provider.CreateAsyncScope();

        var createdAt = await scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>()
            .Send(new StampedContextQuery(), TestContext.Current.CancellationToken);

        createdAt.Should().Be(clock.GetUtcNow().UtcDateTime);
        createdAt.Kind.Should().Be(DateTimeKind.Utc);
    }

    [Fact(DisplayName = "A factory for the default context registered before AddCqrsGenerated replaces the built-in one")]
    public async Task Default_context_factory_registered_before_the_composition_wins()
    {
        // The position a referenced library's discovered factory is in: its registrar TryAdds it before the composition
        // registers the built-in factory.
        await using var provider = Build(before: services => services.AddTransient<IRequestContextFactory<RequestContextBase>, TaggingFactory>());
        await using var scope = provider.CreateAsyncScope();

        var context = await scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>()
            .Send(new DefaultContextProbeQuery(), TestContext.Current.CancellationToken);

        context.Should().BeOfType<TaggedContext>().Which.Tag.Should().Be("replaced");
    }

    [Fact(DisplayName = "A factory for the default context registered after AddCqrsGenerated replaces the built-in one")]
    public async Task Default_context_factory_registered_after_the_composition_wins()
    {
        await using var provider = Build(services => services.AddTransient<IRequestContextFactory<RequestContextBase>>(_ => new TaggingFactory()));
        await using var scope = provider.CreateAsyncScope();

        var context = await scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>()
            .Send(new DefaultContextProbeQuery(), TestContext.Current.CancellationToken);

        context.Should().BeOfType<TaggedContext>().Which.Tag.Should().Be("replaced");
    }

    [Fact(DisplayName = "A factory that returns no context fails the dispatch with its name")]
    public async Task Null_context_is_rejected()
    {
        await using var provider = Build(services => services.AddTransient<IRequestContextFactory<RequestContextBase>, NullFactory>());
        await using var scope = provider.CreateAsyncScope();

        var act = () => scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>()
            .Send(new DefaultContextProbeQuery(), TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage($"*{typeof(NullFactory).FullName}*returned no context*");
    }

    private static ServiceProvider Build(Action<IServiceCollection>? after = null, Action<IServiceCollection>? before = null)
    {
        var services = new ServiceCollection();
        before?.Invoke(services);
        services.AddCqrsGenerated();
        after?.Invoke(services);
        return services.BuildServiceProvider();
    }

    // Private: a factory the generator could see would be registered for every test in the assembly.
    private sealed class TaggingFactory : IRequestContextFactory<RequestContextBase>
    {
        public ValueTask<RequestContextBase> CreateContextAsync(IRequest request, CancellationToken cancellationToken)
            => new(new TaggedContext { Tag = "replaced" });
    }

    private sealed class NullFactory : IRequestContextFactory<RequestContextBase>
    {
        public ValueTask<RequestContextBase> CreateContextAsync(IRequest request, CancellationToken cancellationToken) => new((RequestContextBase)null!);
    }

    private sealed class TaggedContext : RequestContextBase
    {
        public required string Tag { get; init; }
    }
}

public sealed class DefaultContextProbeQuery : QueryBase<RequestContextBase>;

public sealed class DefaultContextProbeQueryHandler : IQueryHandler<DefaultContextProbeQuery, RequestContextBase>
{
    public Task<RequestContextBase> Handle(DefaultContextProbeQuery query, CancellationToken cancellationToken)
        => Task.FromResult(query.Context!);
}

public sealed class StampedContext : RequestContextBase;

public sealed class StampedContextFactory : IRequestContextFactory<StampedContext>
{
    // The parameterless constructor: no timestamp passed, so the dispatcher stamps it.
    public ValueTask<StampedContext> CreateContextAsync(IRequest request, CancellationToken cancellationToken) => new(new StampedContext());
}

public sealed class StampedContextQuery : QueryBase<DateTime, StampedContext>;

public sealed class StampedContextQueryHandler : IQueryHandler<StampedContextQuery, DateTime>
{
    public Task<DateTime> Handle(StampedContextQuery query, CancellationToken cancellationToken)
        => Task.FromResult(query.Context!.CreatedAt);
}
