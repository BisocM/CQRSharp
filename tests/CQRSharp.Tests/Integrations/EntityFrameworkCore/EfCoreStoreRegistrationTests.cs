using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using CQRSharp.Core.Outbox;
using CQRSharp.EntityFrameworkCore;
using CQRSharp.Persistence;
using CQRSharp.Redis;
using CQRSharp.Tests.Core;
using CQRSharp.Tests.Shared;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace CQRSharp.Tests.Integrations.EntityFrameworkCore;

/// <summary>
///     The EF Core registration verbs — <c>UseEntityFrameworkCore&lt;TContext&gt;()</c> on the outbox and idempotency
///     builders and the <c>AddEntityFrameworkCoreOutboxStore</c> / <c>AddEntityFrameworkCoreIdempotencyStore</c>
///     service-collection verbs — end to end on SQLite: the outbox, its inbox and the idempotency store are the
///     application's <c>DbContext</c>'s, a durable notification is stored, claimed and delivered by the processor, and an
///     idempotent command's retry is answered with the original result.
/// </summary>
public sealed class EfCoreStoreRegistrationTests : IAsyncDisposable
{
    private readonly SqliteFileDatabase _database = new();

    [Theory(DisplayName = "Every EF Core registration verb runs the outbox, its inbox and idempotency on the DbContext")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Registration_verbs_run_the_stores_on_the_context(bool builderVerbs)
    {
        await using var provider = await BuildAsync(builderVerbs);
        OutboxDrain.Unwrap(provider.GetRequiredService<IOutboxStore>()).Should().BeOfType<EfCoreOutboxStore<VerbsDbContext>>();
        provider.GetRequiredService<IInboxStore>().Should().BeOfType<EfCoreInboxStore<VerbsDbContext>>();
        provider.GetRequiredService<IIdempotencyStore>().Should().BeOfType<EfCoreIdempotencyStore<VerbsDbContext>>();

        await OutboxTestHarness.PublishAsync(provider, new ParallelProbe(1, "ef-verbs"));
        (await QueryAsync(provider, c => c.Set<OutboxEntity>().CountAsync())).Should().Be(1, "the durable notification was stored in the context's table");

        var processor = provider.GetServices<IHostedService>().OfType<OutboxProcessor>().Single();
        await processor.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await OutboxTestHarness.DrainAsync(provider);
        }
        finally
        {
            await processor.StopAsync(CancellationToken.None);
        }

        provider.GetRequiredService<DeliveryProbe>().Completed.Should().ContainSingle().Which.Key.Should().Be("ef-verbs");
        (await QueryAsync(provider, c => c.Set<OutboxEntity>().SingleAsync())).Status.Should().Be(OutboxMessageStatus.Processed);
        (await QueryAsync(provider, c => c.Set<InboxEntity>().CountAsync())).Should().Be(1, "the delivery was recorded in the context's inbox");

        await using var scope = provider.CreateAsyncScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>();
        var first = await dispatcher.Send(new IssueReceipt { IdempotencyKey = "order-1" }, TestContext.Current.CancellationToken);
        var retry = await dispatcher.Send(new IssueReceipt { IdempotencyKey = "order-1" }, TestContext.Current.CancellationToken);

        retry.Value.Should().Be(first.Value, "the retry is answered with the result the EF Core store kept");
        provider.GetRequiredService<ReceiptCounter>().Issued.Should().Be(1);
    }

    [Fact(DisplayName = "A replaced EF Core store neither checks its context at host start nor keeps purging: the last store registered wins")]
    public async Task Replaced_store_checks_and_purges_nothing()
    {
        var services = new ServiceCollection();
        services.AddCqrsGenerated(b => b.UseOutbox(o => o.Enabled()).UseIdempotency());
        // A context that is not even registered: a store whose context is in use would fail its model check.
        services.AddEntityFrameworkCoreOutboxStore<UnregisteredDbContext>();
        services.AddEntityFrameworkCoreIdempotencyStore<UnregisteredDbContext>();
        services.AddInMemoryOutboxStore();
        services.AddInMemoryIdempotencyStore();
        await using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IOptions<EfCoreOutboxStoreOptions>>().Value.Should().NotBeNull("the model check stands aside");
        provider.GetRequiredService<IOptions<EfCoreIdempotencyStoreOptions>>().Value.Should().NotBeNull();

        foreach (var retention in provider.GetServices<IHostedService>().Where(s => s.GetType().Name.StartsWith("EfCore", StringComparison.Ordinal)))
        {
            await retention.StartAsync(TestContext.Current.CancellationToken);
            var stopped = ((BackgroundService)retention).ExecuteTask!;
            await stopped.WaitAsync(TestContext.Current.CancellationToken);
            stopped.IsCompletedSuccessfully.Should().BeTrue("a retention whose store was replaced ends at once");
        }
    }

    [Fact(DisplayName = "A replaced EF Core outbox store's retention learns it from the registrations, without constructing the replacement")]
    public async Task Replaced_store_is_recognised_without_constructing_the_replacement()
    {
        var constructed = 0;
        var services = new ServiceCollection();
        services.AddCqrsGenerated(b => b.UseOutbox(o => o.Enabled()));
        services.AddEntityFrameworkCoreOutboxStore<UnregisteredDbContext>();
        services.AddRedisOutboxStore(_ =>
        {
            Interlocked.Increment(ref constructed);
            throw new InvalidOperationException("the replacement's connection must not be opened by the EF Core retention");
        });
        await using var provider = services.BuildServiceProvider();

        var retention = provider.GetServices<IHostedService>().OfType<BackgroundService>().Single(s => s.GetType().Name.StartsWith("EfCoreOutboxRetention", StringComparison.Ordinal));
        await retention.StartAsync(TestContext.Current.CancellationToken);
        await retention.ExecuteTask!.WaitAsync(TestContext.Current.CancellationToken);

        retention.ExecuteTask.IsCompletedSuccessfully.Should().BeTrue();
        constructed.Should().Be(0);
    }

    // The builder verbs choose the stores inside AddCqrsGenerated; the service-collection verbs replace the in-memory
    // stores a bare UseOutbox/UseIdempotency falls back to.
    private async Task<ServiceProvider> BuildAsync(bool builderVerbs)
    {
        var json = new JsonSerializerOptions { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(new FakeTimeProvider(new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero)));
        services.AddSingleton<DeliveryProbe>();
        services.AddSingleton<ReceiptCounter>();
        services.AddDbContext<VerbsDbContext>(o => o.UseSqlite(_database.ConnectionString));
        if (builderVerbs)
        {
            services.AddCqrsGenerated(b => b
                .UseOutbox(o => o.UseEntityFrameworkCore<VerbsDbContext>())
                .UseIdempotency(i => i.UseEntityFrameworkCore<VerbsDbContext>().ReplayResultsWith(json)));
        }
        else
        {
            services.AddCqrsGenerated(b => b.UseOutbox(o => o.Enabled()).UseIdempotency(i => i.ReplayResultsWith(json)));
            services.AddEntityFrameworkCoreOutboxStore<VerbsDbContext>();
            services.AddEntityFrameworkCoreIdempotencyStore<VerbsDbContext>();
        }

        OutboxDrain.Observe(services);
        var provider = services.BuildServiceProvider();
        await using (var scope = provider.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<VerbsDbContext>().Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        return provider;
    }

    private static async Task<T> QueryAsync<T>(IServiceProvider provider, Func<VerbsDbContext, Task<T>> query)
    {
        await using var scope = provider.CreateAsyncScope();
        return await query(scope.ServiceProvider.GetRequiredService<VerbsDbContext>());
    }

    public ValueTask DisposeAsync() => _database.DisposeAsync();

    private sealed class UnregisteredDbContext(DbContextOptions<UnregisteredDbContext> options) : DbContext(options);

    private sealed class VerbsDbContext(DbContextOptions<VerbsDbContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.ApplyCqrsOutbox().ApplyCqrsIdempotency();
    }
}
