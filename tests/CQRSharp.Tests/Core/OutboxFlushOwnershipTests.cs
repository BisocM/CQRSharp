using CQRSharp.Abstractions.Interfaces.Handlers;
using CQRSharp.Abstractions.Interfaces.Markers.Command;
using CQRSharp.Abstractions.Interfaces.Outbox;
using CQRSharp.Abstractions.Models.Commands;
using CQRSharp.Abstractions.Models.Outbox;
using CQRSharp.Core.Extensions;
using CQRSharp.Core.Mediation;
using CQRSharp.Tests.Shared;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace CQRSharp.Tests.Core;

/// <summary>
///     Locks outbox flush ownership (5.0.0). In <c>OutboxMode.Enabled</c> a durable notification used to sit in the
///     scoped buffer until a transactional unit-of-work drained it — so one published from a plain command, or from
///     outside any request, was silently dropped when the scope ended. The request now owns the buffer.
/// </summary>
public sealed class OutboxFlushOwnershipTests
{
    [Fact(DisplayName = "Enabled mode: a notification published by a non-transactional command is persisted when it succeeds")]
    public async Task Plain_command_notifications_are_flushed_on_success()
    {
        await using var provider = BuildProvider();
        await using (var scope = provider.CreateAsyncScope())
        {
            var dispatcher = scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>();
            (await dispatcher.Send(new PublishingCommand())).IsSuccess.Should().BeTrue();
        }

        (await PendingAsync(provider)).Should().ContainSingle()
            .Which.NotificationType.Should().Be("test.notification");
    }

    [Fact(DisplayName = "Enabled mode: a notification published outside any request goes straight to the store")]
    public async Task Publish_outside_a_request_is_stored_immediately()
    {
        await using var provider = BuildProvider();
        await using (var scope = provider.CreateAsyncScope())
        {
            var dispatcher = scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>();
            await dispatcher.Publish(new TestNotification());
        }

        (await PendingAsync(provider)).Should().ContainSingle();
    }

    [Fact(DisplayName = "Enabled mode: a failed command's notifications are discarded, not persisted by the next command in the scope")]
    public async Task Failed_command_notifications_are_discarded()
    {
        await using var provider = BuildProvider();
        await using (var scope = provider.CreateAsyncScope())
        {
            var dispatcher = scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>();

            var act = () => dispatcher.Send(new PublishingCommand { ThrowAfterPublish = true });
            await act.Should().ThrowAsync<InvalidOperationException>();

            // Same scope (the default ScopeMode): the failed command's notification must not ride along.
            (await dispatcher.Send(new PublishingCommand())).IsSuccess.Should().BeTrue();
        }

        (await PendingAsync(provider)).Should().ContainSingle("only the successful command's notification is durable");
    }

    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddCqrsGenerated(b => b.UseOutbox(o => o.Enabled().UseInMemoryStore()));
        return services.BuildServiceProvider();
    }

    private static async Task<OutboxMessage[]> PendingAsync(IServiceProvider provider)
    {
        await using var scope = provider.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IOutboxStore>();
        return (await store.GetPendingAsync(100, CancellationToken.None)).ToArray();
    }
}

public sealed class PublishingCommand : CommandBase
{
    public bool ThrowAfterPublish { get; init; }
}

public sealed class PublishingCommandHandler(ICqrsDispatcher dispatcher) : ICommandHandler<PublishingCommand>
{
    public async Task<CommandResult> Handle(PublishingCommand command, CancellationToken cancellationToken)
    {
        await dispatcher.Publish(new TestNotification(), cancellationToken);
        if (command.ThrowAfterPublish) throw new InvalidOperationException("after publish");
        return CommandResult.FromSuccess();
    }
}
