using CQRSharp.Core.Idempotency;
using CQRSharp.Core.Outbox;
using CQRSharp.EntityFrameworkCore;
using CQRSharp.Persistence;
using CQRSharp.Pipelines;
using CQRSharp.Redis;
using CQRSharp.Tests.Shared;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;
using StackExchange.Redis;

namespace CQRSharp.Tests.Integrations;

/// <summary>
///     Which store a container ends up with: an explicit store registration (a builder store verb or a raw
///     <c>Add*Store</c>) replaces whatever store is registered, the outbox and inbox always as a pair, and the builder's
///     implicit in-memory store is only a fallback for a container that has no store at all — whatever the order.
/// </summary>
public sealed class StoreSelectionTests
{
    private static readonly IConnectionMultiplexer Redis = new Mock<IConnectionMultiplexer>().Object;

    [Fact(DisplayName = "A durable outbox store registered after a bare UseOutbox replaces the in-memory fallback, inbox included")]
    public void Outbox_store_registered_after_a_bare_UseOutbox_wins()
    {
        var services = new ServiceCollection();
        services.AddCqrsGenerated(b => b.UseOutbox(o => o.Enabled()));
        services.AddRedisOutboxStore(Redis);

        Registered<IOutboxStore>(services).Should().Be<RedisOutboxStore>();
        Registered<IInboxStore>(services).Should().Be<RedisInboxStore>();
    }

    [Fact(DisplayName = "A durable outbox store registered before a bare UseOutbox is kept: the fallback registers nothing")]
    public void Outbox_store_registered_before_a_bare_UseOutbox_is_kept()
    {
        var services = new ServiceCollection();
        services.AddRedisOutboxStore(Redis);
        services.AddCqrsGenerated(b => b.UseOutbox(o => o.Enabled()));

        Registered<IOutboxStore>(services).Should().Be<RedisOutboxStore>();
        Registered<IInboxStore>(services).Should().Be<RedisInboxStore>();
    }

    [Fact(DisplayName = "A custom outbox store without an inbox is not given the in-memory inbox by a bare UseOutbox")]
    public void Custom_outbox_store_without_an_inbox_gets_no_in_memory_inbox()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IOutboxStore, NullOutboxStore>();
        services.AddCqrsGenerated(b => b.UseOutbox(o => o.Enabled()));

        Registered<IOutboxStore>(services).Should().Be<NullOutboxStore>();
        services.Should().NotContain(d => d.ServiceType == typeof(IInboxStore), "an inbox beside another store's messages would record the wrong deliveries");
    }

    [Fact(DisplayName = "Between two explicit outbox stores the last one wins, and its inbox replaces the other's")]
    public void Last_explicit_outbox_store_wins()
    {
        var services = new ServiceCollection();
        services.AddRedisOutboxStore(Redis);
        services.AddEntityFrameworkCoreOutboxStore<SelectionDbContext>();

        Registered<IOutboxStore>(services).Should().Be<EfCoreOutboxStore<SelectionDbContext>>();
        Registered<IInboxStore>(services).Should().Be<EfCoreInboxStore<SelectionDbContext>>();

        services.AddInMemoryOutboxStore();

        Registered<IOutboxStore>(services).Should().Be<InMemoryOutboxStore>();
        Registered<IInboxStore>(services).Should().Be<InMemoryInboxStore>();
    }

    [Fact(DisplayName = "A UseStore callback that uses TryAdd still replaces the store an earlier AddCqrsGenerated registered")]
    public void UseStore_with_TryAdd_replaces_an_earlier_store()
    {
        var services = new ServiceCollection();
        services.AddCqrsGenerated(b => b.UseOutbox(o => o.Enabled()));
        services.AddCqrsGenerated(b => b.UseOutbox(o => o.UseStore(s => s.TryAddSingleton<IOutboxStore, NullOutboxStore>())));

        Registered<IOutboxStore>(services).Should().Be<NullOutboxStore>();
        services.Should().NotContain(d => d.ServiceType == typeof(IInboxStore), "the chosen store's pair replaces the whole previous pair");
    }

    [Theory(DisplayName = "A durable idempotency store chosen in one AddCqrsGenerated wins over a bare UseIdempotency in another, in either order")]
    [InlineData(true)]
    [InlineData(false)]
    public void Idempotency_store_chosen_explicitly_wins_in_either_order(bool bareFirst)
    {
        var services = new ServiceCollection();
        if (bareFirst) services.AddCqrsGenerated(b => b.UseIdempotency());
        services.AddCqrsGenerated(b => b.UseIdempotency(i => i.UseRedis(Redis)));
        if (!bareFirst) services.AddCqrsGenerated(b => b.UseIdempotency());

        Registered<IIdempotencyStore>(services).Should().Be<RedisIdempotencyStore>();
    }

    [Fact(DisplayName = "A raw idempotency store registered after a bare UseIdempotency replaces the in-memory fallback")]
    public void Idempotency_store_registered_after_a_bare_UseIdempotency_wins()
    {
        var services = new ServiceCollection();
        services.AddCqrsGenerated(b => b.UseIdempotency());
        services.AddRedisIdempotencyStore(Redis);

        Registered<IIdempotencyStore>(services).Should().Be<RedisIdempotencyStore>();

        services.AddInMemoryIdempotencyStore();

        Registered<IIdempotencyStore>(services).Should().Be<InMemoryIdempotencyStore>();
    }

    [Fact(DisplayName = "A bare UseOutbox with no store registered anywhere falls back to the in-memory store and its inbox")]
    public void Bare_UseOutbox_falls_back_to_the_in_memory_store()
    {
        var services = new ServiceCollection();
        services.AddCqrsGenerated(b => b.UseOutbox(o => o.Transactional()));

        Registered<IOutboxStore>(services).Should().Be<InMemoryOutboxStore>();
        Registered<IInboxStore>(services).Should().Be<InMemoryInboxStore>();
    }

    [Fact(DisplayName = "A bare UseIdempotency with no store registered anywhere falls back to the in-memory store")]
    public void Bare_UseIdempotency_falls_back_to_the_in_memory_store()
    {
        var services = new ServiceCollection();
        services.AddCqrsGenerated(b => b.UseIdempotency());

        Registered<IIdempotencyStore>(services).Should().Be<InMemoryIdempotencyStore>();
    }

    [Fact(DisplayName = "A second result serializer replaces the first instead of being registered beside it")]
    public void Result_serializer_is_replaced()
    {
        var first = new Mock<IIdempotencyResultSerializer>().Object;
        var second = new Mock<IIdempotencyResultSerializer>().Object;
        var services = new ServiceCollection();
        services.AddCqrsGenerated(b => b.UseIdempotency(i => i.ReplayResultsWith(first)));
        services.AddCqrsGenerated(b => b.UseIdempotency(i => i.ReplayResultsWith(second)));

        services.Should().ContainSingle(d => d.ServiceType == typeof(IIdempotencyResultSerializer))
            .Which.ImplementationInstance.Should().BeSameAs(second);
    }

    // The one registration of TService, as the type it resolves to; a registration by factory is resolved to find out.
    private static Type Registered<TService>(IServiceCollection services) where TService : notnull
    {
        var descriptor = services.Should().ContainSingle(d => d.ServiceType == typeof(TService), $"exactly one {typeof(TService).Name} is registered").Which;
        if (descriptor.ImplementationType is { } type) return type;
        if (descriptor.ImplementationInstance is { } instance) return instance.GetType();

        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<TService>().GetType();
    }

    private sealed class SelectionDbContext(DbContextOptions<SelectionDbContext> options) : DbContext(options);
}
