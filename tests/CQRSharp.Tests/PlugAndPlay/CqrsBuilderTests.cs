using CQRSharp.Abstractions.Interfaces.Idempotency;
using CQRSharp.Core.Diagnostics;
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
    public async Task Plain_AddCqrs_delegate_overload_binds_to_the_builder()
    {
        // Overload disambiguation: AddCqrs(b => ...) must bind to the ICqrsBuilder overload (the lambda parameter is
        // ICqrsBuilder, proving the binding at compile time), not the AddCqrs(Action<BackgroundTaskQueueOptions>?,...)
        // overload. UseGenerated() is a documented no-op here, so the generated registrations are applied separately.
        var services = new ServiceCollection();
        services.AddCqrs(b => b.UseGenerated().UseLogging());
        services.AddGenerated();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var cqrs = scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>();

        var commandResult = await cqrs.Send(new TestCommand());
        commandResult.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task UseInMemoryIdempotency_registers_a_working_idempotency_store()
    {
        var services = new ServiceCollection();
        services.AddCqrsGenerated(b => b
            .UseIdempotency()
            .UseInMemoryIdempotency());

        using var provider = services.BuildServiceProvider();
        var store = provider.GetService<IIdempotencyStore>();

        store.Should().NotBeNull("UseInMemoryIdempotency registers the in-memory idempotency store");
        (await store!.TryClaimAsync("k", CancellationToken.None)).Should().BeTrue();
        (await store.TryClaimAsync("k", CancellationToken.None)).Should().BeFalse("the key is already claimed");
    }
}
