using System.Diagnostics;
using CQRSharp.Abstractions.Models.Commands;
using CQRSharp.Abstractions.Models.Validation;
using CQRSharp.Core.Background.TaskQueue;
using CQRSharp.Core.Diagnostics;
using CQRSharp.Core.Mediation;
using CQRSharp.Core.Notifications.Types;
using CQRSharp.Pipelines.Behaviors.RateLimiting;
using CQRSharp.Sample.Application.Commands.Handlers;
using CQRSharp.Sample.Application.Commands.Requests;
using CQRSharp.Sample.Application.Pipelines;
using CQRSharp.Sample.Application.Queries.Handlers;
using CQRSharp.Sample.Application.Queries.Requests;
using CQRSharp.Sample.Domain.Entities;
using CQRSharp.Sample.Domain.Events;
using CQRSharp.Sample.Infrastructure.Interceptors;
using CQRSharp.Sample.Infrastructure.SelfTest;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CQRSharp.Sample.Infrastructure.Services;

public sealed class SampleHostedService(
    IServiceScopeFactory scopeFactory,
    SampleUserContext userContext,
    SampleDiagnostics diagnostics,
    IBackgroundTaskManager backgroundTaskManager,
    ILogger<SampleHostedService> logger,
    IHostApplicationLifetime appLifetime)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Environment.ExitCode = 0;

        try
        {
            logger.LogInformation("CQRSharp.Sample SELF-TEST starting...");

            userContext.UserId = $"self-test-{Guid.NewGuid():N}";

            await using var scope = scopeFactory.CreateAsyncScope();
            var provider = scope.ServiceProvider;
            var cqrs = provider.GetRequiredService<ICqrsDispatcher>();
            var cqrsDiagnostics = provider.GetRequiredService<ICqrsDiagnostics>();
            var scopeMarker = provider.GetRequiredService<SampleScopedMarker>();

            await RunBackgroundQueueTestAsync(backgroundTaskManager, diagnostics, stoppingToken).ConfigureAwait(false);
            await RunScopeSemanticsTestAsync(cqrs, scopeMarker.Id, stoppingToken).ConfigureAwait(false);

            var userId = Guid.NewGuid();
            await RunOutboxAndRequestTestAsync(cqrs, diagnostics, userContext.UserId, userId, stoppingToken)
                .ConfigureAwait(false);

            await RunQueryTestAsync(cqrs, diagnostics, userId, stoppingToken).ConfigureAwait(false);
            await RunResultCommandTestAsync(cqrs, stoppingToken).ConfigureAwait(false);
            await RunDynamicSendTestAsync(cqrs, userId, stoppingToken).ConfigureAwait(false);
            await RunStreamingTestAsync(cqrs, diagnostics, scopeMarker.Id, stoppingToken).ConfigureAwait(false);
            await RunStreamExceptionHandlingTestAsync(cqrs, diagnostics, stoppingToken).ConfigureAwait(false);
            await RunValidationTestAsync(cqrs, diagnostics, stoppingToken).ConfigureAwait(false);
            await RunPipelineExemptionTestAsync(cqrs, diagnostics, stoppingToken).ConfigureAwait(false);
            await RunInterceptorTestAsync(cqrs, diagnostics, stoppingToken).ConfigureAwait(false);
            await RunRateLimitingTestAsync(cqrs, userContext, stoppingToken).ConfigureAwait(false);
            await RunResilienceTestAsync(cqrs, stoppingToken).ConfigureAwait(false);
            await RunTimeoutTestAsync(cqrs, stoppingToken).ConfigureAwait(false);
            await RunExceptionHandlingTestAsync(cqrs, diagnostics, stoppingToken).ConfigureAwait(false);
            RunDiagnosticsApiTest(cqrsDiagnostics);

            logger.LogInformation("CQRSharp.Sample SELF-TEST PASSED");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Environment.ExitCode = 1;
            logger.LogCritical(ex, "CQRSharp.Sample SELF-TEST FAILED: {Message}", ex.Message);
        }
        finally
        {
            appLifetime.StopApplication();
        }
    }

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

    private static async Task RunValidationTestAsync(
        ICqrsDispatcher cqrs,
        SampleDiagnostics diagnostics,
        CancellationToken cancellationToken)
    {
        Require(diagnostics.GetValidatedCommandHandlerInvocationCount() == 0,
            "ValidatedCommand handler invocation count should start at zero.");

        try
        {
            await cqrs.Send(new ValidatedCommand(null), cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException("ValidatedCommand should have failed validation but completed successfully.");
        }
        catch (RequestValidationException ex) when (ex.RequestType == typeof(ValidatedCommand))
        {
        }

        Require(diagnostics.GetValidatedCommandHandlerInvocationCount() == 0,
            "ValidatedCommand handler should not be invoked when validation fails.");

        var ok = await cqrs.Send(new ValidatedCommand("ok"), cancellationToken).ConfigureAwait(false);
        Require(ok.IsSuccess, "ValidatedCommand did not succeed.");
        Require(diagnostics.GetValidatedCommandHandlerInvocationCount() == 1,
            "ValidatedCommand handler should be invoked exactly once for the valid request.");
    }

    private static async Task RunPipelineExemptionTestAsync(
        ICqrsDispatcher cqrs,
        SampleDiagnostics diagnostics,
        CancellationToken cancellationToken)
    {
        var result = await cqrs.Send(new PingCommand(), cancellationToken).ConfigureAwait(false);
        Require(result.IsSuccess, "PingCommand did not succeed.");

        Require(diagnostics.GetLoggedRequestCount(typeof(PingCommand)) == 0,
            "PingCommand should be exempted from LoggingPipelineBehavior but was logged.");
    }

    private static async Task RunInterceptorTestAsync(
        ICqrsDispatcher cqrs,
        SampleDiagnostics diagnostics,
        CancellationToken cancellationToken)
    {
        var result = await cqrs.Send(new InterceptorDemoCommand(), cancellationToken).ConfigureAwait(false);
        Require(result.IsSuccess, "InterceptorDemoCommand did not succeed.");

        Require(diagnostics.GetInterceptorPreCount(typeof(InterceptorDemoCommand)) > 0,
            "Expected CustomInterceptorAttribute pre-handler to run.");
        Require(diagnostics.GetInterceptorPostCount(typeof(InterceptorDemoCommand)) > 0,
            "Expected CustomInterceptorAttribute post-handler to run.");
    }

    private static async Task RunRateLimitingTestAsync(
        ICqrsDispatcher cqrs,
        SampleUserContext userContext,
        CancellationToken cancellationToken)
    {
        userContext.UserId = $"rate-test-{Guid.NewGuid():N}";

        var successes = 0;
        var failures = 0;
        for (var i = 0; i < 10; i++)
            try
            {
                await cqrs.Send(new PingCommand(), cancellationToken).ConfigureAwait(false);
                successes++;
            }
            catch (RateLimitExceededException)
            {
                failures++;
            }

        Require(successes > 0, "Expected at least one PingCommand to succeed under rate limiting.");
        Require(failures > 0, "Expected at least one PingCommand to be rate limited.");

        await Task.Delay(TimeSpan.FromMilliseconds(1100), cancellationToken).ConfigureAwait(false);

        var afterDelay = await cqrs.Send(new PingCommand(), cancellationToken).ConfigureAwait(false);
        Require(afterDelay.IsSuccess, "Expected PingCommand to succeed after rate limit tokens replenished.");
    }

    private static async Task RunResilienceTestAsync(
        ICqrsDispatcher cqrs,
        CancellationToken cancellationToken)
    {
        var result = await cqrs.Send(new FailingCommand(), cancellationToken).ConfigureAwait(false);
        Require(result.IsSuccess, "FailingCommand did not succeed after retries.");
    }

    private static async Task RunTimeoutTestAsync(
        ICqrsDispatcher cqrs,
        CancellationToken cancellationToken)
    {
        try
        {
            await cqrs.Send(new SlowCommand(), cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException("SlowCommand should have timed out but completed successfully.");
        }
        catch (TimeoutException)
        {
        }
    }

    private static async Task RunExceptionHandlingTestAsync(
        ICqrsDispatcher cqrs,
        SampleDiagnostics diagnostics,
        CancellationToken cancellationToken)
    {
        Require(diagnostics.GetExceptionActionCount(typeof(ExceptionDemoCommand)) == 0,
            "ExceptionDemoCommand exception action count should start at zero.");
        Require(diagnostics.GetExceptionHandlerCount(typeof(ExceptionDemoCommand)) == 0,
            "ExceptionDemoCommand exception handler count should start at zero.");

        var result = await cqrs.Send(new ExceptionDemoCommand(), cancellationToken).ConfigureAwait(false);
        Require(!result.IsSuccess, "ExceptionDemoCommand should return a failure CommandResult via exception handling.");
        Require(result.ErrorMessage == "Exception handled.", "Unexpected error message from ExceptionDemoCommand.");

        Require(diagnostics.GetExceptionActionCount(typeof(ExceptionDemoCommand)) > 0,
            "Expected exception action to execute for ExceptionDemoCommand.");
        Require(diagnostics.GetExceptionHandlerCount(typeof(ExceptionDemoCommand)) > 0,
            "Expected exception handler to execute for ExceptionDemoCommand.");
    }

    private static void RunDiagnosticsApiTest(ICqrsDiagnostics cqrsDiagnostics)
    {
        var pingBinding = cqrsDiagnostics.DescribeRequest(typeof(PingCommand));
        Require(pingBinding.HandlerType == typeof(PingCommandHandler), "PingCommand binding did not report PingCommandHandler.");
        Require(pingBinding.ResponseType == typeof(CommandResult), "PingCommand binding did not report CommandResult response type.");

        var pingLoggingBehavior = typeof(LoggingPipelineBehavior<PingCommand, CommandResult>);
        Require(!pingBinding.Pipeline.Any(b => b.BehaviorType == pingLoggingBehavior),
            "PingCommand pipeline should exclude LoggingPipelineBehavior but it is still present.");
        Require(pingBinding.ExemptedPipeline.Any(b => b.BehaviorType == pingLoggingBehavior && b.IsExempted),
            "PingCommand diagnostics should report LoggingPipelineBehavior as exempted.");

        var createUserBinding = cqrsDiagnostics.DescribeRequest(typeof(CreateUserCommand));
        Require(createUserBinding.HandlerType == typeof(CreateUserCommandHandler),
            "CreateUserCommand binding did not report CreateUserCommandHandler.");

        var createUserLoggingBehavior = typeof(LoggingPipelineBehavior<CreateUserCommand, CommandResult>);
        Require(createUserBinding.Pipeline.Any(b => b.BehaviorType == createUserLoggingBehavior && !b.IsExempted),
            "CreateUserCommand pipeline should include LoggingPipelineBehavior but it was not reported.");

        var interceptorBinding = cqrsDiagnostics.DescribeRequest(typeof(InterceptorDemoCommand));
        Require(interceptorBinding.HandlerType == typeof(InterceptorDemoCommandHandler),
            "InterceptorDemoCommand binding did not report InterceptorDemoCommandHandler.");
        Require(interceptorBinding.PreHandlers.Any(h => h.AttributeType == typeof(CustomInterceptorAttribute) && h.Priority == 10),
            "InterceptorDemoCommand diagnostics did not report CustomInterceptorAttribute as a pre-handler.");
        Require(interceptorBinding.PostHandlers.Any(h => h.AttributeType == typeof(CustomInterceptorAttribute) && h.Priority == 10),
            "InterceptorDemoCommand diagnostics did not report CustomInterceptorAttribute as a post-handler.");

        var streamBinding = cqrsDiagnostics.DescribeRequest(typeof(StreamProbeRequest));
        Require(streamBinding.HandlerType == typeof(StreamProbeRequestHandler),
            "StreamProbeRequest binding did not report StreamProbeRequestHandler.");
        Require(streamBinding.ResponseType == typeof(IAsyncEnumerable<StreamProbeItem>),
            "StreamProbeRequest binding did not report IAsyncEnumerable<StreamProbeItem> response type.");

        static void RequireSorted(IReadOnlyList<CqrsPipelineBehaviorBinding> bindings, string name)
        {
            for (var i = 1; i < bindings.Count; i++)
            {
                var prev = bindings[i - 1];
                var next = bindings[i];

                if (prev.Priority < next.Priority) continue;
                if (prev.Priority > next.Priority)
                    throw new InvalidOperationException($"{name} is not sorted by priority.");

                var prevName = prev.BehaviorType.FullName;
                var nextName = next.BehaviorType.FullName;
                if (string.CompareOrdinal(prevName, nextName) > 0)
                    throw new InvalidOperationException($"{name} is not deterministically sorted by type name.");
            }
        }

        RequireSorted(pingBinding.Pipeline, "PingCommand pipeline");
        RequireSorted(pingBinding.ExemptedPipeline, "PingCommand exempted pipeline");
        RequireSorted(createUserBinding.Pipeline, "CreateUserCommand pipeline");
        RequireSorted(createUserBinding.ExemptedPipeline, "CreateUserCommand exempted pipeline");
        RequireSorted(streamBinding.Pipeline, "StreamProbeRequest pipeline");
        RequireSorted(streamBinding.ExemptedPipeline, "StreamProbeRequest exempted pipeline");
    }

    private static void RequireNotificationPipelineExecuted(SampleDiagnostics diagnostics, Type notificationType)
    {
        Require(diagnostics.GetNotificationPipelineBeforeCount(notificationType) > 0,
            $"Notification pipeline did not run (before) for {notificationType.Name}.");
        Require(diagnostics.GetNotificationPipelineAfterCount(notificationType) > 0,
            $"Notification pipeline did not run (after) for {notificationType.Name}.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static async Task WaitUntilAsync(
        Func<bool> condition,
        TimeSpan timeout,
        TimeSpan pollInterval,
        CancellationToken cancellationToken)
    {
        var start = Stopwatch.GetTimestamp();
        while (!condition())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (Stopwatch.GetElapsedTime(start) > timeout)
                throw new TimeoutException($"Condition did not become true within {timeout.TotalSeconds:0.##} seconds.");

            await Task.Delay(pollInterval, cancellationToken).ConfigureAwait(false);
        }
    }
}