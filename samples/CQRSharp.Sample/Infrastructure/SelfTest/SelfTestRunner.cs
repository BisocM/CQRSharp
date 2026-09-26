using CQRSharp.Sample.Infrastructure.Identity;
using CQRSharp.Sample.Infrastructure.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CQRSharp.Sample.Infrastructure.SelfTest;

/// <summary>
///     Runs every scenario against the started host and reports whether all of them passed. Each scenario runs in a DI
///     scope of its own, acting for a user of its own, the way separate requests from separate callers do: a scenario's
///     rate-limit bucket and scoped services start fresh whatever the others did.
/// </summary>
public sealed partial class SelfTestRunner(IServiceProvider services, SampleDiagnostics diagnostics, ILogger<SelfTestRunner> logger)
{
    // How long an outbox delivery may take. The processor is signalled when a message is stored, so a delivery normally
    // lands within milliseconds; the bound only turns a lost delivery into a failure instead of a hang.
    private static readonly TimeSpan DeliveryTimeout = TimeSpan.FromSeconds(10);

    public async Task<bool> RunAsync(CancellationToken cancellationToken)
    {
        (string Name, Func<Scenario, CancellationToken, Task> Run)[] scenarios =
        [
            ("background queue", RunBackgroundQueueTestAsync),
            ("scope semantics", RunScopeSemanticsTestAsync),
            ("transactional outbox", RunTransactionalOutboxTestAsync),
            ("outbox serialization", RunOutboxSerializationTestAsync),
            ("value-returning command", RunResultCommandTestAsync),
            ("value-type results", RunValueTypeResultTestAsync),
            ("idempotency and replay", RunIdempotencyTestAsync),
            ("external module", RunExternalModuleTestAsync),
            ("dispatch by runtime type", RunDynamicSendTestAsync),
            ("streaming", RunStreamingTestAsync),
            ("stream exception handling", RunStreamExceptionHandlingTestAsync),
            ("validation", RunValidationTestAsync),
            ("pipeline exemption", RunPipelineExemptionTestAsync),
            ("pre- and post-handlers", RunInterceptorTestAsync),
            ("rate limiting", RunRateLimitingTestAsync),
            ("resilience", RunResilienceTestAsync),
            ("timeout", RunTimeoutTestAsync),
            ("exception handling", RunExceptionHandlingTestAsync),
            ("diagnostics API", RunDiagnosticsApiTestAsync),
            ("queued dispatch", RunQueuedDispatchTestAsync),
            ("Redis store registration", RunRedisRegistrationTestAsync)
        ];

        SampleLog.SelfTestStarting(logger);
        var current = string.Empty;
        try
        {
            foreach (var (name, run) in scenarios)
            {
                current = name;
                SampleLog.ScenarioStarting(logger, name);

                await using var scope = services.CreateAsyncScope();
                var userId = $"{name.Replace(' ', '-')}-{Guid.NewGuid():N}";
                scope.ServiceProvider.GetRequiredService<CurrentUser>().UserId = userId;

                await run(new Scenario(scope.ServiceProvider, userId), cancellationToken);
            }

            SampleLog.SelfTestPassed(logger, scenarios.Length);
            return true;
        }
        catch (Exception ex)
        {
            // A cancelled run (Ctrl+C) did not pass either, so it is reported like any other failure.
            SampleLog.SelfTestFailed(logger, ex, current);
            return false;
        }
    }

    private void RequireNotificationPipelineExecuted(Type notificationType)
    {
        Require(diagnostics.GetNotificationPipelineBeforeCount(notificationType) > 0,
            $"The notification pipeline did not run (before) for {notificationType.Name}.");
        Require(diagnostics.GetNotificationPipelineAfterCount(notificationType) > 0,
            $"The notification pipeline did not run (after) for {notificationType.Name}.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static async Task RequireThrowsAsync<TException>(Func<Task> action, string message) where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException(message);
    }

    /// <summary>The scope a scenario runs in and the user it acts for.</summary>
    private sealed class Scenario(IServiceProvider services, string userId)
    {
        public IServiceProvider Services { get; } = services;
        public string UserId { get; } = userId;
        public ICqrsDispatcher Cqrs => Services.GetRequiredService<ICqrsDispatcher>();
        public Guid ScopeId => Services.GetRequiredService<SampleScopedMarker>().Id;
    }
}
