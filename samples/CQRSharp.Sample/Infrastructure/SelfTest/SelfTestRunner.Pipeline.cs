using CQRSharp.Sample.Application.Commands.Handlers;
using CQRSharp.Sample.Application.Commands.Requests;
using CQRSharp.Sample.Application.Queries.Requests;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CQRSharp.Sample.Infrastructure.SelfTest;

// The pipeline: the built-in behaviors Program.cs turns on, the application's own behavior, pre/post handlers and
// exception hooks.
public sealed partial class SelfTestRunner
{
    private async Task RunValidationTestAsync(Scenario scenario, CancellationToken cancellationToken)
    {
        try
        {
            await scenario.Cqrs.Send(new ValidatedCommand(null), cancellationToken);
            throw new InvalidOperationException("ValidatedCommand passed validation without a value.");
        }
        catch (RequestValidationException ex)
        {
            Require(ex.RequestType == typeof(ValidatedCommand), $"The validation failure names {ex.RequestType.Name}.");
            Require(ex.Failures is [{ MemberName: nameof(ValidatedCommand.Value) }],
                "The validation failure does not name ValidatedCommand.Value.");
        }

        Require(diagnostics.GetRunCount(ValidatedCommandHandler.RunKey) == 0, "ValidatedCommand's handler ran for an invalid request.");

        var ok = await scenario.Cqrs.Send(new ValidatedCommand("ok"), cancellationToken);
        Require(ok.IsSuccess, "ValidatedCommand did not succeed.");
        Require(diagnostics.GetRunCount(ValidatedCommandHandler.RunKey) == 1, "ValidatedCommand's handler did not run once for the valid request.");
    }

    private async Task RunPipelineExemptionTestAsync(Scenario scenario, CancellationToken cancellationToken)
    {
        var result = await scenario.Cqrs.Send(new PingCommand(), cancellationToken);

        Require(result.IsSuccess, "PingCommand did not succeed.");
        Require(diagnostics.GetRequestCount(typeof(PingCommand)) == 0,
            "RequestCountingBehavior wrapped PingCommand, which is exempted from it.");
    }

    private async Task RunInterceptorTestAsync(Scenario scenario, CancellationToken cancellationToken)
    {
        var result = await scenario.Cqrs.Send(new InterceptorDemoCommand(), cancellationToken);

        Require(result.IsSuccess, "InterceptorDemoCommand did not succeed.");
        Require(diagnostics.GetInterceptorPreCount(typeof(InterceptorDemoCommand)) == 1,
            "CustomInterceptorAttribute's pre-handler did not run once.");
        Require(diagnostics.GetInterceptorPostCount(typeof(InterceptorDemoCommand)) == 1,
            "CustomInterceptorAttribute's post-handler did not see the request succeed.");
    }

    private static async Task RunRateLimitingTestAsync(Scenario scenario, CancellationToken cancellationToken)
    {
        var bucketSize = scenario.Services.GetRequiredService<IOptions<RateLimitingOptions>>().Value.MaxTokens;

        // The scenario's user has spent nothing yet, so its bucket starts full.
        var accepted = 0;
        RateLimitExceededException? rejection = null;
        while (rejection is null && accepted <= bucketSize * 2)
        {
            try
            {
                await scenario.Cqrs.Send(new PingCommand(), cancellationToken);
                accepted++;
            }
            catch (RateLimitExceededException ex)
            {
                rejection = ex;
            }
        }

        Require(rejection is not null, $"{accepted} PingCommands in a row were not rate limited (the bucket holds {bucketSize}).");
        Require(accepted >= bucketSize, $"PingCommand was rate limited after {accepted} requests; the bucket holds {bucketSize}.");

        // What a well-behaved client does: wait as long as Retry-After says (whole milliseconds, rounded up, so a timer
        // never waits less), and wait again if a retry is still early (the limiter's clock and the timer's are not the
        // same clock).
        for (var attempt = 1;; attempt++)
        {
            var retryAfter = rejection!.RetryAfter;
            Require(retryAfter > TimeSpan.Zero, "A rate-limit rejection carried no Retry-After.");
            await Task.Delay(retryAfter!.Value, cancellationToken);

            try
            {
                var afterWait = await scenario.Cqrs.Send(new PingCommand(), cancellationToken);
                Require(afterWait.IsSuccess, "PingCommand did not succeed once its bucket had a token again.");
                return;
            }
            catch (RateLimitExceededException ex) when (attempt < 3)
            {
                rejection = ex;
            }
        }
    }

    private async Task RunResilienceTestAsync(Scenario scenario, CancellationToken cancellationToken)
    {
        var retries = scenario.Services.GetRequiredService<IOptions<ResilienceOptions>>().Value.MaxRetries;
        var command = new FailingCommand { FailuresBeforeSuccess = retries };

        var result = await scenario.Cqrs.Send(command, cancellationToken);

        Require(result.IsSuccess, "FailingCommand did not succeed after its retries.");
        var attempts = diagnostics.GetRunCount(FailingCommandHandler.AttemptKey(command));
        Require(attempts == retries + 1, $"FailingCommand ran {attempts} times; expected {retries + 1} (one attempt and {retries} retries).");
    }

    private static Task RunTimeoutTestAsync(Scenario scenario, CancellationToken cancellationToken)
        => RequireThrowsAsync<RequestTimeoutException>(
            () => scenario.Cqrs.Send(new SlowCommand(), cancellationToken),
            "SlowCommand was not stopped by the timeout behavior.");

    private async Task RunExceptionHandlingTestAsync(Scenario scenario, CancellationToken cancellationToken)
    {
        var result = await scenario.Cqrs.Send(new ExceptionDemoCommand(), cancellationToken);

        Require(!result.IsSuccess && result.ErrorMessage == "Exception handled.",
            "ExceptionDemoCommand did not return the exception handler's result.");
        Require(diagnostics.GetExceptionActionCount(typeof(ExceptionDemoCommand)) == 1,
            "The exception action did not run once for ExceptionDemoCommand.");
        Require(diagnostics.GetExceptionHandlerCount(typeof(ExceptionDemoCommand)) == 1,
            "The exception handler did not run once for ExceptionDemoCommand.");
    }

    private async Task RunIdempotencyTestAsync(Scenario scenario, CancellationToken cancellationToken)
    {
        // A retry under the same key is answered with the original result; the handler runs once.
        var receiptKey = $"receipt-{Guid.NewGuid():N}";
        var first = await scenario.Cqrs.Send(new IssueReceiptCommand(receiptKey, 12.5m), cancellationToken);
        var replayed = await scenario.Cqrs.Send(new IssueReceiptCommand(receiptKey, 12.5m), cancellationToken);

        Require(first.IsSuccess && replayed.IsSuccess, "IssueReceiptCommand or its retry failed.");
        Require(replayed.Value == first.Value, $"The retry was answered with receipt {replayed.Value}, not the original {first.Value}.");
        Require(diagnostics.GetRunCount(receiptKey) == 1, "The retry ran IssueReceiptCommand's handler again instead of replaying.");

        // The key's promise holds: a different request under the same key is neither run nor answered.
        await RequireThrowsAsync<IdempotencyKeyMismatchException>(
            () => scenario.Cqrs.Send(new IssueReceiptCommand(receiptKey, 99m), cancellationToken),
            "A reused idempotency key with a different payload was not rejected.");

        // A value-type result is replayed too.
        var quoteKey = $"quote-{Guid.NewGuid():N}";
        var quote = await scenario.Cqrs.Send(new QuoteShippingQuery(quoteKey, 2m), cancellationToken);
        var replayedQuote = await scenario.Cqrs.Send(new QuoteShippingQuery(quoteKey, 2m), cancellationToken);

        Require(replayedQuote == quote, $"The repeated quote returned {replayedQuote}, not the original {quote}.");
        Require(diagnostics.GetRunCount(quoteKey) == 1, "The repeated quote ran QuoteShippingQuery's handler again instead of replaying.");
    }
}
