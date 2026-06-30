using CQRSharp.Abstractions.Attributes.Pipelines;
using CQRSharp.Abstractions.Interfaces.Handlers;
using CQRSharp.Abstractions.Interfaces.Markers.Command;
using CQRSharp.Abstractions.Interfaces.Markers.Request;
using CQRSharp.Abstractions.Models.Commands;
using CQRSharp.Core.Extensions;
using CQRSharp.Core.Mediation;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace CQRSharp.Tests.Core;

/// <summary>
///     Verifies the outcome-aware post-handler (4.1.0): a post-handler deriving from
///     <see cref="OutcomeAwarePostHandlerAttribute" /> receives the request's <see cref="RequestOutcome" /> — the typed
///     returned value on success (so it can classify on a verdict that an otherwise-successful dispatch carried) and the
///     exception on failure, without masking it.
/// </summary>
public sealed class OutcomeAwarePostHandlerTests
{
    [Fact]
    public async Task Post_handler_sees_the_returned_value_on_success()
    {
        await using var provider = BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>();
        var recorder = scope.ServiceProvider.GetRequiredService<OutcomeRecorder>();

        var result = await dispatcher.Send(new VerdictCommand { Verdict = "denied" });

        result.IsSuccess.Should().BeTrue(); // the command succeeded at producing a verdict — no exception
        recorder.Captured.Should().NotBeNull();
        recorder.Captured!.Value.Threw.Should().BeFalse();
        recorder.Captured.Value.Result.Should().BeOfType<CommandResult<string>>()
            .Which.Value.Should().Be("denied"); // the post-handler can read the verdict the dispatch carried
    }

    [Fact]
    public async Task Post_handler_sees_the_exception_and_does_not_mask_it()
    {
        await using var provider = BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>();
        var recorder = scope.ServiceProvider.GetRequiredService<OutcomeRecorder>();

        var act = () => dispatcher.Send(new VerdictCommand { ShouldThrow = true });

        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("boom"); // original surfaces
        recorder.Captured.Should().NotBeNull();
        recorder.Captured!.Value.Threw.Should().BeTrue();
        recorder.Captured.Value.Exception.Should().BeOfType<InvalidOperationException>();
    }

    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddCqrsGenerated();
        services.AddSingleton<OutcomeRecorder>();
        return services.BuildServiceProvider();
    }
}

public sealed class OutcomeRecorder
{
    public RequestOutcome? Captured { get; set; }
}

public sealed class CaptureOutcomeAttribute : OutcomeAwarePostHandlerAttribute
{
    public override int PostHandlerExecutionPriority => 0;

    public override Task OnAfterHandle(IRequest request, RequestOutcome outcome, IServiceProvider serviceProvider, CancellationToken cancellationToken)
    {
        serviceProvider.GetRequiredService<OutcomeRecorder>().Captured = outcome;
        return Task.CompletedTask;
    }
}

[CaptureOutcome]
public sealed class VerdictCommand : ResultCommandBase<string>
{
    public string Verdict { get; init; } = "ok";
    public bool ShouldThrow { get; init; }
}

public sealed class VerdictCommandHandler : IResultCommandHandler<VerdictCommand, string>
{
    public Task<CommandResult<string>> Handle(VerdictCommand command, CancellationToken cancellationToken)
    {
        if (command.ShouldThrow) throw new InvalidOperationException("boom");
        return Task.FromResult(CommandResult<string>.FromSuccess(command.Verdict));
    }
}
