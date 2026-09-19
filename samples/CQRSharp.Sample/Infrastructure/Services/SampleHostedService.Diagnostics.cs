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
