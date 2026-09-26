using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using CQRSharp.Core.Outbox;
using CQRSharp.Persistence;
using CQRSharp.Pipelines;
using CQRSharp.Redis;
using CQRSharp.Tests.Core;
using CQRSharp.Tests.Shared;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using Moq;
using StackExchange.Redis;
using Xunit;

namespace CQRSharp.Tests.Integrations.Redis;

/// <summary>
///     The Redis registration verbs. End to end against the server of <see cref="RedisFixture" />: every verb - the
///     <c>UseRedis</c> builder verbs and the <c>AddRedisOutboxStore</c> / <c>AddRedisIdempotencyStore</c>
///     service-collection verbs, each by connection string, by multiplexer and by connection factory - runs the outbox,
///     its inbox and the idempotency store on Redis. Without a server: which connection the stores run on, which is exactly
///     the one each registration was given, whatever <see cref="IConnectionMultiplexer" /> the application registers
///     itself and in whichever order, so the outbox and the idempotency store can live on different servers; there each
///     connection is a fake that records the store operations sent through it.
/// </summary>
[Collection(RedisCollection.Name)]
public sealed class RedisStoreRegistrationTests(RedisFixture fixture) : IAsyncLifetime
{
    private readonly string _prefix = RedisFixture.NewKeyPrefix("registration");

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync() => await fixture.DeleteKeysAsync(_prefix);

    /// <summary>How a registration is handed its connection.</summary>
    public enum ConnectionKind
    {
        ConnectionString,
        Multiplexer,
        Factory
    }

    [Theory(DisplayName = "Every Redis registration verb runs the outbox, its inbox and idempotency on Redis")]
    [InlineData(true, ConnectionKind.ConnectionString)]
    [InlineData(true, ConnectionKind.Multiplexer)]
    [InlineData(true, ConnectionKind.Factory)]
    [InlineData(false, ConnectionKind.ConnectionString)]
    [InlineData(false, ConnectionKind.Multiplexer)]
    [InlineData(false, ConnectionKind.Factory)]
    public async Task Registration_verbs_run_the_stores_on_Redis(bool builderVerbs, ConnectionKind connection)
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);
        await using var provider = BuildOnServer(builderVerbs, connection);
        OutboxDrain.Unwrap(provider.GetRequiredService<IOutboxStore>()).Should().BeOfType<RedisOutboxStore>();
        provider.GetRequiredService<IInboxStore>().Should().BeOfType<RedisInboxStore>();
        provider.GetRequiredService<IIdempotencyStore>().Should().BeOfType<RedisIdempotencyStore>();

        await OutboxTestHarness.PublishAsync(provider, new ParallelProbe(1, "redis-verbs"));
        (await provider.GetRequiredService<IOutboxStore>().GetBacklogAsync(Cancel)).PendingCount
            .Should().Be(1, "the durable notification was stored in Redis");

        var processor = OutboxTestHarness.Processor(provider);
        await processor.StartAsync(Cancel);
        try
        {
            await OutboxTestHarness.DrainAsync(provider);
        }
        finally
        {
            await processor.StopAsync(CancellationToken.None);
        }

        provider.GetRequiredService<DeliveryProbe>().Completed.Should().ContainSingle().Which.Key.Should().Be("redis-verbs");
        (await provider.GetRequiredService<IOutboxStore>().GetBacklogAsync(Cancel)).PendingCount
            .Should().Be(0, "the delivered message was marked processed");

        await using var scope = provider.CreateAsyncScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>();
        var first = await dispatcher.Send(new IssueReceipt { IdempotencyKey = "order-1" }, Cancel);
        var retry = await dispatcher.Send(new IssueReceipt { IdempotencyKey = "order-1" }, Cancel);

        retry.Value.Should().Be(first.Value, "the retry is answered with the result the Redis store kept");
        provider.GetRequiredService<ReceiptCounter>().Issued.Should().Be(1);
    }

    [Theory(DisplayName = "A connection the application registers itself, before or after, does not replace the one the stores were given")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_applications_own_connection_does_not_replace_the_given_one(bool registeredFirst)
    {
        var app = new FakeConnection();
        var given = new FakeConnection();
        var services = new ServiceCollection();
        if (registeredFirst) services.AddSingleton(app.Multiplexer);
        services.AddRedisOutboxStore(given.Multiplexer).AddRedisIdempotencyStore(given.Multiplexer);
        if (!registeredFirst) services.AddSingleton(app.Multiplexer);

        await using var provider = services.BuildServiceProvider();
        await UseEveryStoreAsync(provider);

        given.Operations.Should().Be(3, "the outbox, the inbox and the idempotency store each ran on the given connection");
        app.Operations.Should().Be(0, "the application's own connection is none of the stores' business");
    }

    [Fact(DisplayName = "The outbox and the idempotency store can run on two different servers")]
    public async Task The_outbox_and_the_idempotency_store_can_use_different_connections()
    {
        var outbox = new FakeConnection();
        var idempotency = new FakeConnection();
        var services = new ServiceCollection();

        var register = () => services.AddRedisOutboxStore(outbox.Multiplexer).AddRedisIdempotencyStore(idempotency.Multiplexer);

        register.Should().NotThrow();
        await using var provider = services.BuildServiceProvider();
        await UseEveryStoreAsync(provider);
        outbox.Operations.Should().Be(2, "the outbox and its inbox run on the outbox's connection");
        idempotency.Operations.Should().Be(1, "the idempotency store runs on its own connection");
    }

    [Fact(DisplayName = "The builder verbs register the stores on the connections they are given")]
    public async Task The_builder_verbs_use_the_connections_they_are_given()
    {
        var outbox = new FakeConnection();
        var idempotency = new FakeConnection();
        var services = new ServiceCollection();
        services.AddCqrsGenerated(b => b
            .UseOutbox(o => o.UseRedis(outbox.Multiplexer))
            .UseIdempotency(i => i.UseRedis(_ => idempotency.Multiplexer)));

        await using var provider = services.BuildServiceProvider();
        await UseEveryStoreAsync(provider);

        outbox.Operations.Should().Be(2);
        idempotency.Operations.Should().Be(1);
    }

    [Fact(DisplayName = "A connection factory runs once for the outbox and its inbox, and can hand the stores the application's own connection")]
    public async Task A_connection_factory_runs_once_for_the_outbox_and_its_inbox()
    {
        var app = new FakeConnection();
        var calls = 0;
        var services = new ServiceCollection();
        services.AddSingleton(app.Multiplexer);
        services.AddRedisOutboxStore(sp =>
        {
            calls++;
            return sp.GetRequiredService<IConnectionMultiplexer>();
        });

        await using var provider = services.BuildServiceProvider();
        await provider.GetRequiredService<IOutboxStore>().GetBacklogAsync(Cancel);
        await provider.GetRequiredService<IInboxStore>().IsDeliveredAsync(Guid.NewGuid(), "h", Cancel);

        calls.Should().Be(1, "the outbox and its inbox share the one connection the factory returned");
        app.Operations.Should().Be(2);
    }

    [Fact(DisplayName = "A second outbox registration replaces the first one's connection along with its stores")]
    public async Task A_second_registration_replaces_the_first_ones_connection()
    {
        var first = new FakeConnection();
        var second = new FakeConnection();
        var services = new ServiceCollection();
        services.AddRedisOutboxStore(first.Multiplexer).AddRedisOutboxStore(second.Multiplexer);

        await using var provider = services.BuildServiceProvider();
        await provider.GetRequiredService<IOutboxStore>().GetBacklogAsync(Cancel);
        await provider.GetRequiredService<IInboxStore>().IsDeliveredAsync(Guid.NewGuid(), "h", Cancel);

        second.Operations.Should().Be(2);
        first.Operations.Should().Be(0);
    }

    [Fact(DisplayName = "No registration puts an IConnectionMultiplexer into the container")]
    public void No_registration_adds_a_connection_to_the_container()
    {
        var services = new ServiceCollection();
        services.AddRedisOutboxStore("redis-a:6379");
        services.AddRedisIdempotencyStore(new FakeConnection().Multiplexer);
        services.AddCqrsGenerated(b => b.UseIdempotency(i => i.UseRedis(_ => new FakeConnection().Multiplexer)));

        services.Should().NotContain(d => d.ServiceType == typeof(IConnectionMultiplexer),
            "the application's container slot for its own Redis connection is left to the application");
    }

    [Fact(DisplayName = "Disposing the service provider leaves a connection the application passed in open")]
    public async Task A_given_connection_is_never_disposed()
    {
        var given = new FakeConnection();
        var fromFactory = new FakeConnection();
        var services = new ServiceCollection();
        services.AddRedisOutboxStore(given.Multiplexer).AddRedisIdempotencyStore(_ => fromFactory.Multiplexer);

        await using (var provider = services.BuildServiceProvider())
            await UseEveryStoreAsync(provider);

        given.WasClosed.Should().BeFalse("the application owns the connection it passed in");
        fromFactory.WasClosed.Should().BeFalse("the application owns the connection its factory returned");
    }

    [Fact(DisplayName = "A connection factory that returns null fails the store's resolution, naming the registration")]
    public async Task A_connection_factory_that_returns_null_fails_the_resolution()
    {
        var services = new ServiceCollection();
        services.AddRedisIdempotencyStore(_ => null!);

        await using var provider = services.BuildServiceProvider();
        var resolve = () => provider.GetRequiredService<IIdempotencyStore>();

        resolve.Should().Throw<InvalidOperationException>().WithMessage("*AddRedisIdempotencyStore returned null*");
    }

    [Theory(DisplayName = "A blank connection string is rejected when the store is registered, not when it is first used")]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_connection_string_is_rejected_at_registration(string connectionString)
    {
        var services = new ServiceCollection();

        FluentActions.Invoking(() => services.AddRedisOutboxStore(connectionString)).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => services.AddRedisIdempotencyStore(connectionString)).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => services.AddCqrsGenerated(b => b.UseOutbox(o => o.UseRedis(connectionString))))
            .Should().Throw<ArgumentException>();
    }

    // The builder verbs choose the stores inside AddCqrsGenerated; the service-collection verbs replace the in-memory
    // stores a bare UseOutbox/UseIdempotency falls back to. A factory hands the stores the connection the application
    // registered itself. Every key goes under the test's own prefix.
    private ServiceProvider BuildOnServer(bool builderVerbs, ConnectionKind connection)
    {
        var json = new JsonSerializerOptions { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(new FakeTimeProvider(new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero)));
        services.AddSingleton<DeliveryProbe>();
        services.AddSingleton<ReceiptCounter>();
        if (connection == ConnectionKind.Factory)
            services.AddSingleton(fixture.Multiplexer);

        if (builderVerbs)
        {
            services.AddCqrsGenerated(b => b
                .UseOutbox(o => UseRedisOutbox(o))
                .UseIdempotency(i => UseRedisIdempotency(i).ReplayResultsWith(json)));
        }
        else
        {
            services.AddCqrsGenerated(b => b.UseOutbox(o => o.Enabled()).UseIdempotency(i => i.ReplayResultsWith(json)));
            AddRedisStores(services);
        }

        OutboxDrain.Observe(services);
        return services.BuildServiceProvider();

        OutboxStoreBuilder UseRedisOutbox(OutboxStoreBuilder o) => connection switch
        {
            ConnectionKind.ConnectionString => o.UseRedis(fixture.ConnectionString, Outbox),
            ConnectionKind.Multiplexer => o.UseRedis(fixture.Multiplexer, Outbox),
            _ => o.UseRedis(Registered, Outbox)
        };

        IdempotencyStoreBuilder UseRedisIdempotency(IdempotencyStoreBuilder i) => connection switch
        {
            ConnectionKind.ConnectionString => i.UseRedis(fixture.ConnectionString, Idempotency),
            ConnectionKind.Multiplexer => i.UseRedis(fixture.Multiplexer, Idempotency),
            _ => i.UseRedis(Registered, Idempotency)
        };

        IServiceCollection AddRedisStores(IServiceCollection s) => connection switch
        {
            ConnectionKind.ConnectionString => s
                .AddRedisOutboxStore(fixture.ConnectionString, Outbox)
                .AddRedisIdempotencyStore(fixture.ConnectionString, Idempotency),
            ConnectionKind.Multiplexer => s
                .AddRedisOutboxStore(fixture.Multiplexer, Outbox)
                .AddRedisIdempotencyStore(fixture.Multiplexer, Idempotency),
            _ => s
                .AddRedisOutboxStore(Registered, Outbox)
                .AddRedisIdempotencyStore(Registered, Idempotency)
        };

        void Outbox(RedisOutboxOptions o) => o.KeyPrefix = _prefix;
        void Idempotency(RedisIdempotencyOptions o) => o.KeyPrefix = _prefix;
        static IConnectionMultiplexer Registered(IServiceProvider sp) => sp.GetRequiredService<IConnectionMultiplexer>();
    }

    // One operation per store: the outbox's backlog, the inbox's lookup and the idempotency store's release.
    private static async Task UseEveryStoreAsync(IServiceProvider provider)
    {
        await provider.GetRequiredService<IOutboxStore>().GetBacklogAsync(Cancel);
        await provider.GetRequiredService<IInboxStore>().IsDeliveredAsync(Guid.NewGuid(), "h", Cancel);
        await provider.GetRequiredService<IIdempotencyStore>().ReleaseAsync("key", "token", Cancel);
    }

    // A connection that answers every store operation with an empty result and counts them.
    private sealed class FakeConnection
    {
        private readonly Mock<IConnectionMultiplexer> _multiplexer = new();
        private readonly Mock<IDatabase> _database = new();

        public FakeConnection()
        {
            _multiplexer.Setup(m => m.GetDatabase(It.IsAny<int>(), It.IsAny<object?>())).Returns(_database.Object);
            _database.Setup(d => d.KeyExistsAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>())).ReturnsAsync(false);
            _database.Setup(d => d.ScriptEvaluateAsync(It.IsAny<string>(), It.IsAny<RedisKey[]>(), It.IsAny<RedisValue[]>(), It.IsAny<CommandFlags>()))
                .ReturnsAsync(RedisResult.Create(
                [
                    RedisResult.Create((RedisValue)0),
                    RedisResult.Create((RedisValue)0),
                    RedisResult.Create(RedisValue.EmptyString)
                ]));
        }

        public IConnectionMultiplexer Multiplexer => _multiplexer.Object;

        public int Operations => _database.Invocations.Count;

        public bool WasClosed => _multiplexer.Invocations.Any(i => i.Method.Name is "Dispose" or "DisposeAsync" or "Close" or "CloseAsync");
    }
}
