using System.Runtime.CompilerServices;
using CQRSharp.Abstractions.Attributes.Pipelines;
using CQRSharp.Abstractions.Interfaces.Handlers;
using CQRSharp.Abstractions.Interfaces.Markers.Command;
using CQRSharp.Abstractions.Interfaces.Markers.Request;
using CQRSharp.Abstractions.Interfaces.Markers.Stream;
using CQRSharp.Abstractions.Interfaces.Notifications;
using CQRSharp.Abstractions.Models.Commands;
using CQRSharp.Core.Extensions;
using CQRSharp.Core.Mediation;
using CQRSharp.Core.Notifications.Types;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace CQRSharp.Tests.Core;

/// <summary>
///     Locks the request lifecycle contract (5.0.0): once <c>*Initiated</c> is published the request always reaches a
///     terminal <c>*Failed</c> notification and its outcome-aware post-handlers, whichever stage throws, and nothing on
///     that failure path can replace the exception the caller sees. Streams follow the same contract.
/// </summary>
public sealed class RequestLifecycleContractTests
{
    [Fact(DisplayName = "A throwing pre-handler still produces CommandFailed and reaches the post-handlers")]
    public async Task PreHandler_failure_is_a_terminal_failure()
    {
        await using var provider = BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>();
        var probe = scope.ServiceProvider.GetRequiredService<LifecycleProbe>();

        var act = () => dispatcher.Send(new GuardedCommand());

        (await act.Should().ThrowAsync<UnauthorizedAccessException>()).WithMessage("denied by pre-handler");
        probe.HandlerRan.Should().BeFalse();
        probe.FailedNotifications.Should().ContainSingle()
            .Which.Should().BeOfType<UnauthorizedAccessException>();
        probe.Outcomes.Should().ContainSingle().Which.Threw.Should().BeTrue();
    }

    [Fact(DisplayName = "A faulting CommandFailed subscriber never masks the handler's exception")]
    public async Task Failed_subscriber_cannot_mask_the_original_exception()
    {
        await using var provider = BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>();
        var probe = scope.ServiceProvider.GetRequiredService<LifecycleProbe>();
        probe.FailedSubscriberThrows = true;

        var act = () => dispatcher.Send(new ExplodingCommand());

        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("handler exploded");
        probe.Outcomes.Should().ContainSingle("the post-handlers still run after a faulting subscriber")
            .Which.Exception.Should().BeOfType<InvalidOperationException>();
    }

    [Fact(DisplayName = "A faulted stream reaches the outcome-aware post-handlers with its exception")]
    public async Task Stream_failure_reaches_post_handlers()
    {
        await using var provider = BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>();
        var probe = scope.ServiceProvider.GetRequiredService<LifecycleProbe>();

        var items = new List<int>();
        var act = async () =>
        {
            await foreach (var item in dispatcher.Stream(new ExplodingStream()))
                items.Add(item);
        };

        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("stream exploded");
        items.Should().Equal(1);
        probe.Outcomes.Should().ContainSingle().Which.Exception.Should().BeOfType<InvalidOperationException>();
    }

    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddCqrsGenerated();
        services.AddScoped<LifecycleProbe>();
        return services.BuildServiceProvider();
    }
}

/// <summary>Per-scope recorder. Only these tests register it, so the fixtures below are inert everywhere else.</summary>
public sealed class LifecycleProbe
{
    public bool HandlerRan { get; set; }
    public bool FailedSubscriberThrows { get; set; }
    public List<Exception> FailedNotifications { get; } = [];
    public List<RequestOutcome> Outcomes { get; } = [];
}

public sealed class DenyAttribute : Attribute, IPreHandlerAttribute
{
    public int PreHandlerExecutionPriority => 0;

    public Task OnBeforeHandle(IRequest request, IServiceProvider serviceProvider, CancellationToken cancellationToken)
        => throw new UnauthorizedAccessException("denied by pre-handler");
}

public sealed class RecordOutcomeAttribute : Attribute, IPostHandlerAttribute
{
    public int PostHandlerExecutionPriority => 0;

    public Task OnAfterHandle(IRequest request, RequestOutcome outcome, IServiceProvider serviceProvider, CancellationToken cancellationToken)
    {
        serviceProvider.GetService<LifecycleProbe>()?.Outcomes.Add(outcome);
        return Task.CompletedTask;
    }
}

[Deny]
[RecordOutcome]
public sealed class GuardedCommand : CommandBase;

public sealed class GuardedCommandHandler(IServiceProvider services) : ICommandHandler<GuardedCommand>
{
    public Task<CommandResult> Handle(GuardedCommand command, CancellationToken cancellationToken)
    {
        if (services.GetService<LifecycleProbe>() is { } probe) probe.HandlerRan = true;
        return Task.FromResult(CommandResult.FromSuccess());
    }
}

[RecordOutcome]
public sealed class ExplodingCommand : CommandBase;

public sealed class ExplodingCommandHandler : ICommandHandler<ExplodingCommand>
{
    public Task<CommandResult> Handle(ExplodingCommand command, CancellationToken cancellationToken)
        => throw new InvalidOperationException("handler exploded");
}

[RecordOutcome]
public sealed class ExplodingStream : StreamRequestBase<int>;

public sealed class ExplodingStreamHandler : IStreamRequestHandler<ExplodingStream, int>
{
    public async IAsyncEnumerable<int> Handle(ExplodingStream request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return 1;
        await Task.Yield();
        throw new InvalidOperationException("stream exploded");
    }
}

/// <summary>Subscribes to every failed command in the test assembly, but only acts when the probe is registered.</summary>
public sealed class LifecycleFailedSubscriber(IServiceProvider services) : INotificationHandler<CommandFailedNotification>
{
    public Task Handle(CommandFailedNotification notification, CancellationToken cancellationToken)
    {
        if (services.GetService<LifecycleProbe>() is not { } probe) return Task.CompletedTask;

        probe.FailedNotifications.Add(notification.Exception);
        if (probe.FailedSubscriberThrows) throw new ApplicationException("subscriber fault");
        return Task.CompletedTask;
    }
}
