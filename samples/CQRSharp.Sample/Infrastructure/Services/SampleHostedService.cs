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

public sealed partial class SampleHostedService(
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
            await RunExternalModuleTestAsync(cqrs, stoppingToken).ConfigureAwait(false);
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
}
