using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CQRSharp.Tests.Core;

/// <summary>
///     The in-memory stores' options are validated by one validator however many registrations add them, and a duration
///     their clock arithmetic cannot take is rejected when the options are built rather than on every claim or sweep.
/// </summary>
public sealed class InMemoryStoreOptionsValidationTests
{
    [Fact(DisplayName = "An invalid in-memory outbox option registered by several store registrations is reported once")]
    public void Outbox_store_option_failure_is_reported_once()
    {
        var services = new ServiceCollection();
        services.AddInMemoryOutboxStore(o => o.VisibilityTimeout = TimeSpan.Zero);
        services.AddInMemoryOutboxStore();
        services.AddCqrsGenerated(b => b.UseOutbox(o => o.UseInMemoryStore()));
        using var provider = services.BuildServiceProvider();

        var act = () => provider.GetRequiredService<IOptions<InMemoryOutboxStoreOptions>>().Value;

        act.Should().Throw<OptionsValidationException>().Which.Failures.Should().ContainSingle()
            .Which.Should().Contain("VisibilityTimeout");
    }

    [Fact(DisplayName = "An invalid in-memory idempotency option registered by several store registrations is reported once")]
    public void Idempotency_store_option_failure_is_reported_once()
    {
        var services = new ServiceCollection();
        services.AddInMemoryIdempotencyStore(o => o.Retention = TimeSpan.Zero);
        services.AddCqrsGenerated(b => b.UseIdempotency(i => i.UseInMemoryStore()));
        using var provider = services.BuildServiceProvider();

        var act = () => provider.GetRequiredService<IOptions<InMemoryIdempotencyStoreOptions>>().Value;

        act.Should().Throw<OptionsValidationException>().Which.Failures.Should().ContainSingle()
            .Which.Should().Contain("Retention");
    }

    [Theory(DisplayName = "A duration too long for the in-memory stores' clock arithmetic is rejected")]
    [InlineData(nameof(InMemoryOutboxStoreOptions.VisibilityTimeout))]
    [InlineData(nameof(InMemoryOutboxStoreOptions.InboxRetention))]
    [InlineData(nameof(InMemoryOutboxStoreOptions.DeadLetterRetention))]
    [InlineData(nameof(InMemoryIdempotencyStoreOptions.Retention))]
    public void Unbounded_duration_is_rejected(string option)
    {
        var services = new ServiceCollection();
        services.AddInMemoryOutboxStore(o =>
        {
            if (option == nameof(o.VisibilityTimeout)) o.VisibilityTimeout = TimeSpan.MaxValue;
            if (option == nameof(o.InboxRetention)) o.InboxRetention = TimeSpan.MaxValue;
            if (option == nameof(o.DeadLetterRetention)) o.DeadLetterRetention = TimeSpan.MaxValue;
        });
        services.AddInMemoryIdempotencyStore(o =>
        {
            if (option == nameof(o.Retention)) o.Retention = TimeSpan.MaxValue;
        });
        using var provider = services.BuildServiceProvider();

        var outbox = () => provider.GetRequiredService<IOptions<InMemoryOutboxStoreOptions>>().Value;
        var idempotency = () => provider.GetRequiredService<IOptions<InMemoryIdempotencyStoreOptions>>().Value;

        var act = option == nameof(InMemoryIdempotencyStoreOptions.Retention) ? (Action)(() => idempotency()) : () => outbox();
        act.Should().Throw<OptionsValidationException>().WithMessage($"*{option}*10 years*");
    }
}
