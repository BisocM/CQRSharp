using System;
using System.Threading;
using System.Threading.Tasks;
using CQRSharp.Abstractions.Interfaces.Handlers;
using CQRSharp.Abstractions.Interfaces.Markers.Command;
using CQRSharp.Abstractions.Models.Commands;
using CQRSharp.Core.Extensions;
using CQRSharp.Core.Mediation;
using CQRSharp.Core.Options.Enums;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace CQRSharp.Tests.Core;

/// <summary>
///     Proves the <see cref="RunMode.Queued" /> safety net: when the background consumer never starts (e.g. the provider
///     is built without a running Generic Host), a queued dispatch fails with a clear error after the configured
///     timeout instead of hanging forever on a task nothing will ever complete.
/// </summary>
public sealed class QueuedConsumerGuardTests
{
    [Fact(DisplayName = "RunMode.Queued without a started consumer throws a clear error instead of hanging")]
    public async Task QueuedDispatch_WithoutStartedConsumer_ThrowsClearError()
    {
        var fakeTime = new FakeTimeProvider();
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(fakeTime); // before AddCqrs so its TryAdd defers to the fake clock
        services.AddCqrsGenerated(
            configureQueue: o => o.ConsumerStartTimeout = TimeSpan.FromSeconds(5),
            configureDispatcher: o => o.RunMode = RunMode.Queued);

        // Build the provider but DO NOT start the host — the BackgroundTaskQueueConsumer hosted service never runs.
        var provider = services.BuildServiceProvider();
        var cqrs = provider.CreateScope().ServiceProvider.GetRequiredService<ICqrsDispatcher>();

        // The dispatch parks on the consumer-readiness wait (Task.Delay on the fake clock). Advancing past the
        // timeout makes the guard fire.
        var sendTask = cqrs.Send(new QueuedGuardCommand());
        fakeTime.Advance(TimeSpan.FromSeconds(6));

        var act = async () => await sendTask;
        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*RunMode.Queued requires a running host*");
    }
}

internal sealed class QueuedGuardCommand : CommandBase;

internal sealed class QueuedGuardCommandHandler : ICommandHandler<QueuedGuardCommand>
{
    public Task<CommandResult> Handle(QueuedGuardCommand command, CancellationToken cancellationToken)
        => Task.FromResult(CommandResult.FromSuccess());
}
