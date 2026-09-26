using CQRSharp.Core.Diagnostics;
using CQRSharp.Pipelines;
using CQRSharp.Core.Notifications;
using CQRSharp.Core.Transports;
using CQRSharp.Tests.Shared;
using CQRSharp.Transports;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CQRSharp.Tests.Core;

/// <summary>
///     The configuration rules of the notification transports, at startup (<see cref="ICqrsDiagnostics.DescribeConfiguration" />,
///     which the startup validator reports) and at their first use: <c>CQRCONF013</c> a transport without the outbox,
///     <c>CQRCONF014</c> a forwarded type or name that cannot be stored, <c>CQRCONF015</c> a transport name that clashes,
///     <c>CQRCONF016</c> a forwarded notification published outside a transaction under <c>Transactional</c>, and
///     <c>CQRCONF017</c> a notification taken in that no local handler receives.
/// </summary>
public sealed class TransportConfigurationTests
{
    private const string Forwarded = "tests.transport.forwarded";
    private const string Shared = "tests.transport.shared";

    private static ServiceProvider Build(Action<ICqrsBuilder>? configure, params INotificationTransport[] transports)
        => Build(configure, null, transports);

    private static ServiceProvider Build(Action<ICqrsBuilder>? configure, ILoggerProvider? logs, params INotificationTransport[] transports)
    {
        var services = new ServiceCollection();
        services.AddSingleton<SharedNotificationLog>();
        services.AddSingleton<DeliveryProbe>();
        if (logs is not null) services.AddSingleton(logs);
        foreach (var transport in transports)
            services.AddSingleton(transport);
        services.AddCqrsGenerated(b => configure?.Invoke(b));
        return services.BuildServiceProvider();
    }

    private static IReadOnlyList<CqrsBindingIssue> Describe(IServiceProvider provider)
    {
        using var scope = provider.CreateScope();
        return scope.ServiceProvider.GetRequiredService<ICqrsDiagnostics>().DescribeConfiguration()
            .Where(i => i.Code is "CQRCONF013" or "CQRCONF014" or "CQRCONF015" or "CQRCONF016" or "CQRCONF017")
            .ToArray();
    }

    private static async Task PublishAsync(IServiceProvider provider, INotification notification)
    {
        await using var scope = provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>().Publish(notification);
    }

    [Fact(DisplayName = "CQRCONF013: a transport registered while the outbox is off is an error, and nothing else is checked")]
    public async Task A_transport_without_the_outbox_is_reported()
    {
        await using var provider = Build(null, new ScriptedTransport("broker", Forwarded) { Declaration = new() { PublishedTypes = [typeof(UnnamedForwardedNotification)] } });

        var issue = Describe(provider).Should().ContainSingle().Subject;
        issue.Code.Should().Be("CQRCONF013");
        issue.Severity.Should().Be(CqrsBindingIssueSeverity.Error);
        issue.Message.Should().Contain("'broker'").And.Contain("UseOutbox");
    }

    [Fact(DisplayName = "CQRCONF014: a forwarded type the serializer does not name, or a name no module has, is an error")]
    public async Task Unstorable_publications_are_reported()
    {
        var transport = new ScriptedTransport("broker")
        {
            Declaration = new()
            {
                PublishedTypes = [typeof(UnnamedForwardedNotification), typeof(ForwardedNotification)],
                PublishedNames = ["tests.transport.nobody-has-this", Shared]
            }
        };
        await using var provider = Build(b => b.UseOutbox(o => o.UseInMemoryStore()), transport);

        var issues = Describe(provider);

        issues.Should().HaveCount(2).And.OnlyContain(i => i.Code == "CQRCONF014" && i.Severity == CqrsBindingIssueSeverity.Error);
        issues.Should().Contain(i => i.Message.Contains(typeof(UnnamedForwardedNotification).FullName!) && i.Message.Contains("[NotificationName]"));
        issues.Should().Contain(i => i.Message.Contains("'tests.transport.nobody-has-this'"));
    }

    [Fact(DisplayName = "CQRCONF014 at first use: publishing a type a transport forwards but the serializer does not name fails")]
    public async Task Publishing_an_unnamed_forwarded_type_fails()
    {
        var transport = new ScriptedTransport("broker") { Declaration = new() { PublishedTypes = [typeof(UnnamedForwardedNotification)] } };
        await using var provider = Build(b => b.UseOutbox(o => o.UseInMemoryStore()), transport);

        var act = () => PublishAsync(provider, new UnnamedForwardedNotification(1));

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("CQRCONF014").And.Contain("'broker'");
    }

    [Fact(DisplayName = "CQRCONF015: two transports with one name, or a transport named like a handler, are errors, and the outbox cannot be used")]
    public async Task Clashing_transport_names_are_reported_and_fail_the_first_use()
    {
        var handlerName = "CQRSharp.Tests.Core.SharedNotificationHandler";
        await using var provider = Build(
            b => b.UseOutbox(o => o.UseInMemoryStore()),
            new ScriptedTransport("broker"), new ScriptedTransport("broker"), new ScriptedTransport(handlerName));
        provider.GetRequiredService<INotificationSubscriptionRegistry>().Subscriptions
            .Should().Contain(s => s.HandlerName == handlerName, "the clash is with a real handler name");

        var issues = Describe(provider);

        issues.Should().HaveCount(2).And.OnlyContain(i => i.Code == "CQRCONF015" && i.Severity == CqrsBindingIssueSeverity.Error);
        issues.Should().Contain(i => i.Message.Contains("2 notification transports are named 'broker'"));
        issues.Should().Contain(i => i.Message.Contains($"'{handlerName}' is also the name of a notification handler"));
        provider.GetRequiredService<NotificationTransportRegistry>().Failure.Should().Contain("CQRCONF015");

        // Nothing that could reach a transport runs: storing a durable notification fails, whatever it is.
        var act = () => PublishAsync(provider, new SharedNotification(1));
        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("CQRCONF015");
    }

    [Fact(DisplayName = "CQRCONF015: the outbox processor fails as it starts rather than deliver to an ambiguous name")]
    public async Task The_processor_fails_to_start_on_a_clash()
    {
        await using var provider = Build(b => b.UseOutbox(o => o.UseInMemoryStore()), new ScriptedTransport("broker"), new ScriptedTransport("broker"));
        var processor = OutboxTestHarness.Processor(provider);

        var act = async () =>
        {
            await processor.StartAsync(CancellationToken.None);
            await processor.ExecuteTask!;
        };

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("CQRCONF015");
        await processor.StopAsync(CancellationToken.None);
    }

    [Theory(DisplayName = "CQRCONF015: an empty transport name, or one longer than a handler name may be, is an error")]
    [InlineData("")]
    [InlineData(null)]
    public async Task An_invalid_transport_name_is_reported(string? name)
    {
        await using var provider = Build(b => b.UseOutbox(o => o.UseInMemoryStore()), new ScriptedTransport(name ?? new string('t', 257)));

        Describe(provider).Should().ContainSingle().Which.Message.Should().Contain("not a valid outbox handler name");
    }

    [Fact(DisplayName = "CQRCONF016: under Transactional, forwarding is a warning at startup and a forwarded publish outside a transaction fails")]
    public async Task Forwarding_outside_a_transaction_fails_under_transactional_mode()
    {
        var unitOfWork = new RecordingUnitOfWork();
        var transport = new ScriptedTransport("broker", Shared) { Declaration = new() { PublishedTypes = [typeof(SharedNotification)] } };
        await using var provider = Build(b => b.UseOutbox(o => o.Transactional().UseInMemoryStore()).UseUnitOfWork(_ => unitOfWork), transport);

        Describe(provider).Should().ContainSingle().Which.Should().Match<CqrsBindingIssue>(i =>
            i.Code == "CQRCONF016" && i.Severity == CqrsBindingIssueSeverity.Warning);

        var outside = () => PublishAsync(provider, new SharedNotification(1));
        (await outside.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("CQRCONF016").And.Contain("'broker'");
        provider.GetRequiredService<SharedNotificationLog>().Received.Should().BeEmpty("the failed publish was not delivered in-process either");

        // A notification no transport forwards is delivered in-process outside a transaction, as always.
        await PublishAsync(provider, new ParallelProbe(1, null));
        provider.GetRequiredService<DeliveryProbe>().Completed.Should().ContainSingle();

        unitOfWork.HasActiveTransaction = true;
        await PublishAsync(provider, new SharedNotification(2));
        OutboxTestHarness.Stored(provider).Select(m => m.HandlerName).Should().Contain("broker", "inside a transaction it is stored, for the transport too");
    }

    [Fact(DisplayName = "CQRCONF017: a notification taken in that no local handler receives is a warning at startup and at the transports' first use")]
    public async Task Consumed_without_subscribers_is_reported()
    {
        var logs = new CapturingLoggerProvider();
        var transport = new ScriptedTransport("broker")
        {
            Declaration = new()
            {
                ConsumedTypes = [typeof(ForwardedNotification), typeof(SharedNotification)],
                ConsumedNames = ["tests.transport.nobody-has-this", Forwarded, Shared]
            }
        };
        await using var provider = Build(b => b.UseOutbox(o => o.UseInMemoryStore()), logs, transport);

        var issues = Describe(provider);

        issues.Should().HaveCount(3).And.OnlyContain(i => i.Code == "CQRCONF017" && i.Severity == CqrsBindingIssueSeverity.Warning);
        issues.Count(i => i.Message.Contains(typeof(ForwardedNotification).FullName!)).Should().Be(2, "by type and by name");
        issues.Should().Contain(i => i.Message.Contains("'tests.transport.nobody-has-this'"));

        provider.GetRequiredService<NotificationTransportRegistry>();
        logs.WithId(1207).Should().HaveCount(3).And.OnlyContain(e => e.Level == LogLevel.Warning && e.Message.Contains("CQRCONF017"));
    }

    [Fact(DisplayName = "AddTransport registers a transport with the outbox and composes across UseOutbox calls")]
    public async Task AddTransport_composes()
    {
        var first = new ScriptedTransport("first");
        var second = new ScriptedTransport("second");
        await using var provider = Build(b => b
            .UseOutbox(o => o.UseInMemoryStore().AddTransport(s => s.AddSingleton<INotificationTransport>(first)))
            .UseOutbox(o => o.AddTransport(s => s.AddSingleton<INotificationTransport>(second))));

        provider.GetRequiredService<NotificationTransportRegistry>().Transports.Should().Equal(first, second);
        Describe(provider).Should().BeEmpty();
    }
}
