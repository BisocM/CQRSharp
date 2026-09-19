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
using CQRSharp.Sample.ExternalModule;
using CQRSharp.Sample.Infrastructure.SelfTest;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CQRSharp.Sample.Infrastructure.Services;

public sealed partial class SampleHostedService
{
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
}
