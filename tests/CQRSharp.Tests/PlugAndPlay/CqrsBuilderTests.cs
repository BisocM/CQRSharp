using CQRSharp.Abstractions.Interfaces.Idempotency;
using CQRSharp.Abstractions.Interfaces.Outbox;
using CQRSharp.Core.Diagnostics;
using CQRSharp.Core.Options;
using CQRSharp.Core.Options.Enums;
using Microsoft.Extensions.Options;
using CQRSharp.Core.Extensions;
using CQRSharp.Core.Mediation;
using CQRSharp.Pipelines.Behaviors.RateLimiting;
using CQRSharp.Pipelines.Extensions;
using CQRSharp.Tests.Shared;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace CQRSharp.Tests.PlugAndPlay;

/// <summary>
///     Verifies the fluent <see cref="ICqrsBuilder" />: that verb order never changes the resulting registrations or
///     the resolved pipeline (the builder records intent and applies it in one canonical sequence), that
///     <c>UseTimeProvider</c> is authoritative regardless of position, that the generated fluent overload yields a
///     working dispatcher, and that the plain delegate overload binds unambiguously.
/// </summary>
public sealed class CqrsBuilderTests
{
    // Two deliberately different verb orderings of the SAME configuration. If the builder is order-insensitive, both
    // must produce identical registrations and identical pipelines.
    private static void ConfigureOrderA(ICqrsBuilder b) => b
        .ConfigureQueue(o => o.EnableMetrics = true)
        .UseLogging()
        .UseRateLimiting(o =>
        {
            o.MaxTokens = 5;
            o.ReplenishRatePerSecond = 5;
            o.Scope = RateLimitScope.Global;
        })
        .UseTimeout(o => o.Timeout = TimeSpan.FromSeconds(5))
        .UseResilience(o => o.MaxRetries = 2)
        .ValidateOnStart();

    private static void ConfigureOrderB(ICqrsBuilder b) => b
        .ValidateOnStart()
        .UseResilience(o => o.MaxRetries = 2)
        .UseTimeout(o => o.Timeout = TimeSpan.FromSeconds(5))
        .UseRateLimiting(o =>
        {
            o.MaxTokens = 5;
            o.ReplenishRatePerSecond = 5;
            o.Scope = RateLimitScope.Global;
        })
        .UseLogging()
        .ConfigureQueue(o => o.EnableMetrics = true);

    private static IReadOnlyList<(string? Service, string? Impl, ServiceLifetime Lifetime)> DescriptorSignature(
        IServiceCollection services)
        => services
            .Select(d => (Service: d.ServiceType.FullName, Impl: d.ImplementationType?.FullName, d.Lifetime))
            .OrderBy(x => x.Service, StringComparer.Ordinal)
            .ThenBy(x => x.Impl, StringComparer.Ordinal)
            .ThenBy(x => x.Lifetime)
            .ToArray();

    private static IReadOnlyList<(Type BehaviorType, int Priority)> ResolvedPipeline(IServiceProvider provider)
    {
        using var scope = provider.CreateScope();
        var diagnostics = scope.ServiceProvider.GetRequiredService<ICqrsDiagnostics>();
        return diagnostics.DescribeRequest(typeof(TestCommand)).Pipeline
            .Select(b => (b.BehaviorType, b.Priority))
            .ToArray();
    }

    [Fact]
    public void Verb_order_does_not_change_registrations_or_pipeline()
    {
        var servicesA = new ServiceCollection();
        servicesA.AddCqrsGenerated(ConfigureOrderA);

        var servicesB = new ServiceCollection();
        servicesB.AddCqrsGenerated(ConfigureOrderB);

        // Same registrations (by service type / implementation type / lifetime), independent of verb order.
        DescriptorSignature(servicesA).Should().BeEquivalentTo(DescriptorSignature(servicesB));

        using var providerA = servicesA.BuildServiceProvider();
        using var providerB = servicesB.BuildServiceProvider();

        // Same resolved pipeline (behavior types in the same execution order).
        ResolvedPipeline(providerA).Should().Equal(ResolvedPipeline(providerB));
    }

    [Fact]
    public async Task Verb_order_yields_a_working_dispatcher_both_ways()
    {
        foreach (var configure in new Action<ICqrsBuilder>[] { ConfigureOrderA, ConfigureOrderB })
        {
            var services = new ServiceCollection();
            services.AddCqrsGenerated(configure);

            using var provider = services.BuildServiceProvider();
            using var scope = provider.CreateScope();
            var cqrs = scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>();

            var commandResult = await cqrs.Send(new TestCommand());
            commandResult.IsSuccess.Should().BeTrue();

            var queryResult = await cqrs.Send(new TestQuery());
            queryResult.Value.Should().Be("Success");
        }
    }

    [Fact]
    public void UseTimeProvider_wins_regardless_of_position()
    {
        var fake = new FakeTimeProvider();

        // TimeProvider set first...
        var servicesFirst = new ServiceCollection();
        servicesFirst.AddCqrsGenerated(b => b
            .UseTimeProvider(fake)
            .UseLogging()
            .ValidateOnStart());

        // ...and set last. Either way it must be the authoritative TimeProvider.
        var servicesLast = new ServiceCollection();
        servicesLast.AddCqrsGenerated(b => b
            .UseLogging()
            .ValidateOnStart()
            .UseTimeProvider(fake));

        using var providerFirst = servicesFirst.BuildServiceProvider();
        using var providerLast = servicesLast.BuildServiceProvider();

        providerFirst.GetRequiredService<TimeProvider>().Should().BeSameAs(fake);
        providerLast.GetRequiredService<TimeProvider>().Should().BeSameAs(fake);
    }

    [Fact]
    public void UseTimeProvider_factory_that_resolves_TimeProvider_fails_fast_instead_of_hanging()
    {
        // A factory that resolves TimeProvider from the provider is self-referential: the factory IS the TimeProvider
        // registration, so sp.GetService<TimeProvider>() re-enters it. Before the guard this recursed until the DI
        // container deadlocked (a latent production hang; the ?? TimeProvider.System fallback is unreachable dead code).
        // It must now fail fast with a clear error on first resolution.
        var services = new ServiceCollection();
        services.AddCqrsGenerated(b => b
            .UseTimeProvider(sp => sp.GetService<TimeProvider>() ?? TimeProvider.System));

        using var provider = services.BuildServiceProvider();

        var resolve = () => provider.GetRequiredService<TimeProvider>();

        resolve.Should().Throw<InvalidOperationException>("the self-referential factory must fail fast, not deadlock")
            .WithMessage("*resolves TimeProvider from the service*");
    }

    [Fact]
    public void UseTimeProvider_factory_resolving_a_distinct_clock_type_still_works()
    {
        // The guard must not break the legitimate factory pattern: resolving a DIFFERENT clock type from the provider.
        var clock = new FakeTimeProvider();
        var services = new ServiceCollection();
        services.AddSingleton(clock); // registered under FakeTimeProvider, so RemoveAll<TimeProvider> leaves it intact
        services.AddCqrsGenerated(b => b
            .UseTimeProvider(sp => sp.GetRequiredService<FakeTimeProvider>()));

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<TimeProvider>().Should().BeSameAs(clock);
    }

    [Fact]
    public async Task Generated_fluent_overload_resolves_a_working_dispatcher()
    {
        var services = new ServiceCollection();
        services.AddCqrsGenerated(b => b
            .UseValidation()
            .UseExceptionHandling());

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var cqrs = scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>();

        var commandResult = await cqrs.Send(new TestCommand());
        commandResult.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task UseIdempotency_with_in_memory_store_registers_a_working_store()
    {
        // One cohesive verb selects the behavior and the store together.
        var services = new ServiceCollection();
        services.AddCqrsGenerated(b => b.UseIdempotency(i => i.UseInMemoryStore()));

        using var provider = services.BuildServiceProvider();
        var store = provider.GetService<IIdempotencyStore>();

        store.Should().NotBeNull("UseIdempotency(i => i.UseInMemoryStore()) registers the in-memory idempotency store");
        (await store!.TryClaimAsync("k", CancellationToken.None)).Should().BeTrue();
        (await store.TryClaimAsync("k", CancellationToken.None)).Should().BeFalse("the key is already claimed");
    }

    [Fact]
    public async Task UseIdempotency_defaults_to_the_in_memory_store()
    {
        // A bare UseIdempotency() pulls its own store (the in-memory default) — the verb is no longer half a feature.
        var services = new ServiceCollection();
        services.AddCqrsGenerated(b => b.UseIdempotency());

        using var provider = services.BuildServiceProvider();
        var store = provider.GetService<IIdempotencyStore>();

        store.Should().NotBeNull("a bare UseIdempotency() falls back to the in-memory store");
        (await store!.TryClaimAsync("k", CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public void UseOutbox_enables_the_mode_and_registers_a_store()
    {
        // One cohesive verb sets the mode and the store together.
        var services = new ServiceCollection();
        services.AddCqrsGenerated(b => b.UseOutbox(o => o.Transactional().UseInMemoryStore()));

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IOptions<OutboxOptions>>().Value.Mode.Should().Be(OutboxMode.Transactional);
        provider.GetService<IOutboxStore>().Should().NotBeNull("UseOutbox registers the selected store");
    }

    [Fact]
    public void UseOutbox_defaults_to_the_in_memory_store()
    {
        var services = new ServiceCollection();
        services.AddCqrsGenerated(b => b.UseOutbox(o => o.Transactional()));

        using var provider = services.BuildServiceProvider();
        provider.GetService<IOutboxStore>().Should().NotBeNull("a UseOutbox without a chosen store falls back to in-memory");
    }

    [Fact]
    public void Without_UseOutbox_the_outbox_is_off_by_default()
    {
        // The honest default (#2): no outbox configuration ⇒ Mode is Disabled and no store is registered.
        var services = new ServiceCollection();
        services.AddCqrsGenerated();

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IOptions<OutboxOptions>>().Value.Mode.Should().Be(OutboxMode.Disabled);
        provider.GetService<IOutboxStore>().Should().BeNull("the outbox is off unless explicitly enabled");
    }
}
