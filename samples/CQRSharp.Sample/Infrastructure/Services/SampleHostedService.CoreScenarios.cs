using System.Diagnostics;
using CQRSharp.Core.Background.TaskQueue;
using CQRSharp.Core.Diagnostics;
using CQRSharp.Pipelines;
using CQRSharp.Pipelines.Behaviors.RateLimiting;
using CQRSharp.Sample.Application.Commands.Handlers;
using CQRSharp.Sample.Application.Commands.Requests;
using CQRSharp.Sample.Application.Pipelines;
using CQRSharp.Sample.Application.Queries.Handlers;
using CQRSharp.Sample.Application.Queries.Requests;
using CQRSharp.Sample.Domain.Entities;
using CQRSharp.Sample.Domain.Events;
using CQRSharp.Sample.Infrastructure.Interceptors;
using CQRSharp.Sample.ExternalModule;
using CQRSharp.Sample.Infrastructure.SelfTest;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CQRSharp.Sample.Infrastructure.Services;

public sealed partial class SampleHostedService
{
    private static async Task RunBackgroundQueueTestAsync(
        IBackgroundTaskManager backgroundTaskManager,
        SampleDiagnostics diagnostics,
        CancellationToken cancellationToken)
    {
        const int expected = 42;
        var actual = await backgroundTaskManager.EnqueueAsync(_ => Task.FromResult(expected), cancellationToken);

        Require(actual == expected, $"Background queue returned {actual} (expected {expected}).");

        await WaitUntilAsync(
            () => diagnostics.GetNotificationPipelineAfterCount(typeof(TaskEnqueuedNotification)) > 0,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMilliseconds(25),
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task RunOutboxAndRequestTestAsync(
        ICqrsDispatcher cqrs,
        SampleDiagnostics diagnostics,
        string expectedUserId,
        Guid userId,
        CancellationToken cancellationToken)
    {
        var createUser = new CreateUserCommand("BisocM", userId);
        var commandResult = await cqrs.Send(createUser, cancellationToken).ConfigureAwait(false);

        Require(commandResult.IsSuccess, "CreateUserCommand did not succeed.");
        Require(diagnostics.GetLoggedRequestCount(typeof(CreateUserCommand)) > 0,
            "Expected CreateUserCommand to be logged by LoggingPipelineBehavior.");
        var context = createUser.Context ?? throw new InvalidOperationException("CreateUserCommand context was not created.");
        Require(context.UserId == expectedUserId,
            $"CreateUserCommand context userId '{context.UserId}' did not match expected '{expectedUserId}'.");

        // The notification only reaches its handler after the transactional outbox stored the message, the processor
        // claimed it, dispatched it, and marked it processed. Waiting for it is therefore the end-to-end proof that the
        // (now Core-shipped, in-process) outbox store works — no need to peek at the store's private state.
        var userCreated = await diagnostics.WaitForUserCreatedAsync(userId, TimeSpan.FromSeconds(5), cancellationToken)
            .ConfigureAwait(false);
        Require(userCreated.UserId == userId, "UserCreatedNotification handler observed the wrong userId.");

        // CommandInitiated/CommandCompleted are published synchronously during Send above, so their pipeline counts are
        // already recorded here.
        RequireNotificationPipelineExecuted(diagnostics, typeof(CommandInitiatedNotification));
        RequireNotificationPipelineExecuted(diagnostics, typeof(CommandCompletedNotification));

        // UserCreatedNotification is dispatched asynchronously by the outbox processor, so its pipeline's "after" stage
        // completes on the processor thread a moment after the handler signals WaitForUserCreatedAsync above. Wait for the
        // pipeline to finish before asserting it, rather than racing the processor thread.
        await WaitUntilAsync(
            () => diagnostics.GetNotificationPipelineBeforeCount(typeof(UserCreatedNotification)) > 0
                  && diagnostics.GetNotificationPipelineAfterCount(typeof(UserCreatedNotification)) > 0,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMilliseconds(25),
            cancellationToken).ConfigureAwait(false);
        RequireNotificationPipelineExecuted(diagnostics, typeof(UserCreatedNotification));
    }

    private static async Task RunScopeSemanticsTestAsync(
        ICqrsDispatcher cqrs,
        Guid expectedMarkerId,
        CancellationToken cancellationToken)
    {
        var command = new ScopeProbeCommand();
        var result = await cqrs.Send(command, cancellationToken).ConfigureAwait(false);
        Require(result.IsSuccess, "ScopeProbeCommand did not succeed.");

        var observed = command.HandlerScopeId ?? throw new InvalidOperationException("ScopeProbeCommand did not observe a scoped marker id.");
        Require(observed == expectedMarkerId,
            $"Expected request to execute in current scope. Marker id was '{observed}', expected '{expectedMarkerId}'.");
    }

    private static async Task RunQueryTestAsync(
        ICqrsDispatcher cqrs,
        SampleDiagnostics diagnostics,
        Guid userId,
        CancellationToken cancellationToken)
    {
        var user = await cqrs.Send(new GetUserQuery(userId), cancellationToken).ConfigureAwait(false);
        if (user is null) throw new InvalidOperationException("GetUserQuery returned null.");
        Require(user.Id == userId, "GetUserQuery returned the wrong user.");

        RequireNotificationPipelineExecuted(diagnostics, typeof(QueryInitiatedNotification<User?>));
        RequireNotificationPipelineExecuted(diagnostics, typeof(QueryCompletedNotification<User?>));
    }

    private static async Task RunResultCommandTestAsync(
        ICqrsDispatcher cqrs,
        CancellationToken cancellationToken)
    {
        // A value-returning command (ICommand<TResult>): the minted value flows back in CommandResult<TResult>.
        CommandResult<string> result = await cqrs.Send(new MintTokenCommand { Subject = "svc" }, cancellationToken)
            .ConfigureAwait(false);

        Require(result.IsSuccess, "MintTokenCommand did not succeed.");
        Require(result.Value == "token:svc", $"MintTokenCommand returned an unexpected value '{result.Value}'.");
    }

    private static async Task RunExternalModuleTestAsync(
        ICqrsDispatcher cqrs,
        CancellationToken cancellationToken)
    {
        // A query whose handler lives (internal) in a SEPARATE assembly (CQRSharp.Sample.ExternalModule). Proves the
        // composition root's single AddCqrsGenerated wires a referenced assembly's module — under Native AOT.
        var greeting = await cqrs.Send(new ExternalGreetingQuery { Name = "Sample" }, cancellationToken)
            .ConfigureAwait(false);

        Require(greeting == "hello from the external module, Sample",
            $"ExternalGreetingQuery returned an unexpected value '{greeting}'.");
    }

    private static async Task RunDynamicSendTestAsync(
        ICqrsDispatcher cqrs,
        Guid userId,
        CancellationToken cancellationToken)
    {
        object ping = new PingCommand();
        var pingResult = await cqrs.Send(ping, cancellationToken).ConfigureAwait(false);
        Require(pingResult is CommandResult { IsSuccess: true }, "Dynamic Send(object) did not succeed for PingCommand.");

        object query = new GetUserQuery(userId);
        var user = await cqrs.Send(query, cancellationToken).ConfigureAwait(false);
        Require(user is User { Id: var id } && id == userId, "Dynamic Send(object) returned the wrong user.");
    }

    private static async Task RunStreamingTestAsync(
        ICqrsDispatcher cqrs,
        SampleDiagnostics diagnostics,
        Guid expectedMarkerId,
        CancellationToken cancellationToken)
    {
        var typed = new List<StreamProbeItem>();
        await foreach (var item in cqrs.Stream(new StreamProbeRequest(3), cancellationToken))
            typed.Add(item);

        Require(typed.Count == 3, $"Expected 3 streamed items, got {typed.Count}.");
        Require(typed[0].Value == 1 && typed[1].Value == 2 && typed[2].Value == 3, "StreamProbeRequest values were incorrect.");
        Require(typed.All(x => x.ScopedMarkerId == expectedMarkerId), "StreamProbeRequest observed an unexpected scoped marker id.");

        object untypedRequest = new StreamProbeRequest(2);
        var boxed = new List<object?>();
        await foreach (var item in cqrs.Stream(untypedRequest, cancellationToken))
            boxed.Add(item);

        Require(boxed.Count == 2, $"Expected 2 boxed streamed items, got {boxed.Count}.");
        Require(boxed[0] is StreamProbeItem { Value: 1 } && boxed[1] is StreamProbeItem { Value: 2 },
            "Untyped Stream(object) returned unexpected items.");

        RequireNotificationPipelineExecuted(diagnostics, typeof(StreamInitiatedNotification<StreamProbeItem>));
        RequireNotificationPipelineExecuted(diagnostics, typeof(StreamCompletedNotification<StreamProbeItem>));
    }

    private static async Task RunStreamExceptionHandlingTestAsync(
        ICqrsDispatcher cqrs,
        SampleDiagnostics diagnostics,
        CancellationToken cancellationToken)
    {
        Require(diagnostics.GetExceptionActionCount(typeof(ExceptionDemoStreamRequest)) == 0,
            "ExceptionDemoStreamRequest exception action count should start at zero.");
        Require(diagnostics.GetExceptionHandlerCount(typeof(ExceptionDemoStreamRequest)) == 0,
            "ExceptionDemoStreamRequest exception handler count should start at zero.");

        var results = new List<ExceptionDemoStreamItem>();
        await foreach (var item in cqrs.Stream(new ExceptionDemoStreamRequest(), cancellationToken))
            results.Add(item);

        Require(results.Count == 2 && results[0].Value == -1 && results[1].Value == -2,
            "ExceptionDemoStreamRequest returned unexpected items via exception handling.");
        Require(diagnostics.GetExceptionActionCount(typeof(ExceptionDemoStreamRequest)) > 0,
            "Expected exception action to execute for ExceptionDemoStreamRequest.");
        Require(diagnostics.GetExceptionHandlerCount(typeof(ExceptionDemoStreamRequest)) > 0,
            "Expected exception handler to execute for ExceptionDemoStreamRequest.");
    }
}
