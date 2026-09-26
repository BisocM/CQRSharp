using CQRSharp.Redis;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using StackExchange.Redis;

namespace CQRSharp.Tests.Integrations.Redis;

/// <summary>
///     The Redis store options are checked when the host starts. A key prefix must carry a hash tag, which is what keeps
///     every key a store script touches in one Redis Cluster slot: without one, a cluster rejects the scripts (the outbox's
///     with "Script attempted to access a non local key", the idempotency store's in the client, as a multi-slot command),
///     so the misconfiguration is reported at start, naming the prefix, on every deployment.
/// </summary>
public sealed class RedisStoreOptionsValidationTests
{
    private static readonly IConnectionMultiplexer Connection = new Mock<IConnectionMultiplexer>().Object;

    [Theory(DisplayName = "A prefix has a hash tag when a non-empty text sits between its first '{' and the first '}' after it")]
    [InlineData("{cqrsharp:outbox}:", true)]
    [InlineData("app:{orders}:outbox:", true)]
    [InlineData("}{a}", true)]
    [InlineData("{a}{}", true)]
    [InlineData("cqrsharp:outbox:", false)]
    [InlineData("{}:", false)]
    [InlineData("{}{a}:", false)]
    [InlineData("{cqrsharp:outbox:", false)]
    [InlineData("cqrsharp}:outbox{", false)]
    public void Hash_tags_are_read_as_Redis_reads_them(string prefix, bool hasHashTag)
        => RedisKeyPrefix.HasHashTag(prefix).Should().Be(hasHashTag);

    [Fact(DisplayName = "The default options of both stores are valid")]
    public void The_defaults_are_valid()
    {
        var services = new ServiceCollection();
        services.AddRedisOutboxStore(Connection).AddRedisIdempotencyStore(Connection);
        using var provider = services.BuildServiceProvider();

        FluentActions.Invoking(() => provider.GetRequiredService<IStartupValidator>().Validate()).Should().NotThrow();
    }

    [Fact(DisplayName = "An outbox key prefix without a hash tag fails host start, naming the prefix")]
    public void An_outbox_prefix_without_a_hash_tag_fails_host_start()
    {
        var services = new ServiceCollection();
        services.AddRedisOutboxStore(Connection, o => o.KeyPrefix = "myapp:outbox:");
        using var provider = services.BuildServiceProvider();

        FluentActions.Invoking(() => provider.GetRequiredService<IStartupValidator>().Validate())
            .Should().Throw<OptionsValidationException>()
            .Which.Failures.Should().ContainSingle().Which.Should().Contain("'myapp:outbox:'").And.Contain("hash tag");
    }

    [Fact(DisplayName = "An idempotency key prefix without a hash tag fails host start, naming the prefix")]
    public void An_idempotency_prefix_without_a_hash_tag_fails_host_start()
    {
        var services = new ServiceCollection();
        services.AddCqrsGenerated(b => b.UseIdempotency(i => i.UseRedis(Connection, o => o.KeyPrefix = "myapp:idemp:")));
        using var provider = services.BuildServiceProvider();

        FluentActions.Invoking(() => provider.GetRequiredService<IStartupValidator>().Validate())
            .Should().Throw<OptionsValidationException>()
            .Which.Failures.Should().ContainSingle().Which.Should().Contain("'myapp:idemp:'").And.Contain("hash tag");
    }

    [Fact(DisplayName = "A store registered twice reports each invalid option once")]
    public void A_repeated_registration_reports_a_failure_once()
    {
        var services = new ServiceCollection();
        services.AddRedisOutboxStore(Connection, o => o.KeyPrefix = "untagged:");
        services.AddRedisOutboxStore(Connection);
        services.AddRedisIdempotencyStore(Connection, o => o.Retention = TimeSpan.Zero);
        services.AddRedisIdempotencyStore(Connection);
        using var provider = services.BuildServiceProvider();

        FluentActions.Invoking(() => provider.GetRequiredService<IOptions<RedisOutboxOptions>>().Value)
            .Should().Throw<OptionsValidationException>().Which.Failures.Should().ContainSingle();
        FluentActions.Invoking(() => provider.GetRequiredService<IOptions<RedisIdempotencyOptions>>().Value)
            .Should().Throw<OptionsValidationException>().Which.Failures.Should().ContainSingle();
    }

    [Fact(DisplayName = "Durations below the millisecond Redis counts in, and a negative database other than -1, are rejected")]
    public void Sub_millisecond_durations_and_invalid_databases_are_rejected()
    {
        var services = new ServiceCollection();
        services.AddRedisOutboxStore(Connection, o =>
        {
            o.VisibilityTimeout = TimeSpan.FromTicks(1);
            o.DeadLetterRetention = TimeSpan.FromTicks(1);
            o.InboxRetention = TimeSpan.Zero;
            o.Database = -2;
        });
        services.AddRedisIdempotencyStore(Connection, o =>
        {
            o.Retention = TimeSpan.FromTicks(1);
            o.Database = -2;
        });
        using var provider = services.BuildServiceProvider();

        FluentActions.Invoking(() => provider.GetRequiredService<IOptions<RedisOutboxOptions>>().Value)
            .Should().Throw<OptionsValidationException>().Which.Failures.Should().HaveCount(4);
        FluentActions.Invoking(() => provider.GetRequiredService<IOptions<RedisIdempotencyOptions>>().Value)
            .Should().Throw<OptionsValidationException>().Which.Failures.Should().HaveCount(2);
    }
}
