using CQRSharp.Core.Diagnostics;
using CQRSharp.Sample.Application.Commands.Handlers;
using CQRSharp.Sample.Application.Commands.Requests;
using CQRSharp.Sample.Application.Notifications.Handlers;
using CQRSharp.Sample.Application.Pipelines;
using CQRSharp.Sample.Application.Queries.Handlers;
using CQRSharp.Sample.Application.Queries.Requests;
using CQRSharp.Sample.Domain.Entities;
using CQRSharp.Sample.Domain.Events;
using CQRSharp.Sample.ExternalModule;
using CQRSharp.Sample.Infrastructure.Interceptors;
using Microsoft.Extensions.DependencyInjection;

namespace CQRSharp.Sample.Infrastructure.SelfTest;

// Dispatch itself: scopes, result shapes, a second assembly's module, dispatch by runtime type, streams and the
// diagnostics API.
public sealed partial class SelfTestRunner
{
    private static async Task RunScopeSemanticsTestAsync(Scenario scenario, CancellationToken cancellationToken)
    {
        var handlerScopeId = await scenario.Cqrs.Send(new ScopeProbeQuery(), cancellationToken);

        Require(handlerScopeId == scenario.ScopeId,
            $"The handler ran in scope '{handlerScopeId}', not in the dispatcher's scope '{scenario.ScopeId}'.");
    }

    private static async Task RunResultCommandTestAsync(Scenario scenario, CancellationToken cancellationToken)
    {
        // A value-returning command (ICommand<TResult>): the minted value flows back in CommandResult<TResult>.
        var result = await scenario.Cqrs.Send(new MintTokenCommand { Subject = "svc" }, cancellationToken);

        Require(result.IsSuccess, "MintTokenCommand did not succeed.");
        Require(result.Value == "token:svc", $"MintTokenCommand returned an unexpected value '{result.Value}'.");
    }

    // Value-type results and value-type notifications are the shapes Native AOT treats differently: the container cannot
    // close an open-generic behavior over them, so the generated closed factories must be the ones that run the pipeline
    // here.
    private async Task RunValueTypeResultTestAsync(Scenario scenario, CancellationToken cancellationToken)
    {
        var sum = await scenario.Cqrs.Send(new AddNumbersQuery(20, 22), cancellationToken);
        Require(sum == 42, $"AddNumbersQuery returned {sum}.");
        Require(diagnostics.GetRequestCount(typeof(AddNumbersQuery)) > 0,
            "RequestCountingBehavior did not wrap AddNumbersQuery (a value-type result).");

        var items = new List<int>();
        await foreach (var item in scenario.Cqrs.Stream(new CountdownStreamRequest(3), cancellationToken))
            items.Add(item);

        Require(items.SequenceEqual([3, 2, 1]), $"CountdownStreamRequest streamed [{string.Join(", ", items)}].");
        Require(diagnostics.GetStreamBehaviorCount(typeof(CountdownStreamRequest)) > 0,
            "StreamProbeBehavior did not wrap CountdownStreamRequest (value-type items).");

        // The notification counterpart: a value-type notification published through NotificationLoggingBehavior<>.
        await scenario.Cqrs.Publish(new SensorReadingNotification(21.5), cancellationToken);
        Require(diagnostics.GetRunCount(SensorReadingNotificationHandler.RunKey) == 1,
            "SensorReadingNotification (a value type) did not reach its handler once.");
        RequireNotificationPipelineExecuted(typeof(SensorReadingNotification));
    }

    private static async Task RunExternalModuleTestAsync(Scenario scenario, CancellationToken cancellationToken)
    {
        // The handler is internal to CQRSharp.Sample.ExternalModule: only that assembly's generated module can register
        // it, and this app's single AddCqrsGenerated wires that module.
        var greeting = await scenario.Cqrs.Send(new ExternalGreetingQuery { Name = "Sample" }, cancellationToken);

        Require(greeting == "hello from the external module, Sample",
            $"ExternalGreetingQuery returned an unexpected value '{greeting}'.");
    }

    private static async Task RunDynamicSendTestAsync(Scenario scenario, CancellationToken cancellationToken)
    {
        object ping = new PingCommand();
        var pingResult = await scenario.Cqrs.Send(ping, cancellationToken);
        Require(pingResult is CommandResult { IsSuccess: true }, "Send(object) did not succeed for PingCommand.");

        object sum = new AddNumbersQuery(1, 2);
        var boxed = await scenario.Cqrs.Send(sum, cancellationToken);
        Require(boxed is 3, $"Send(object) returned '{boxed}' for AddNumbersQuery.");
    }

    private async Task RunStreamingTestAsync(Scenario scenario, CancellationToken cancellationToken)
    {
        var typed = new List<StreamProbeItem>();
        await foreach (var item in scenario.Cqrs.Stream(new StreamProbeRequest(3), cancellationToken))
            typed.Add(item);

        Require(typed.Select(x => x.Value).SequenceEqual([1, 2, 3]),
            $"StreamProbeRequest streamed [{string.Join(", ", typed.Select(x => x.Value))}].");
        Require(typed.All(x => x.ScopeId == scenario.ScopeId), "StreamProbeRequest's handler ran outside the dispatcher's scope.");

        object untypedRequest = new StreamProbeRequest(2);
        var boxed = new List<object?>();
        await foreach (var item in scenario.Cqrs.Stream(untypedRequest, cancellationToken))
            boxed.Add(item);

        Require(boxed is [StreamProbeItem { Value: 1 }, StreamProbeItem { Value: 2 }],
            "Stream(object) returned unexpected items.");

        RequireNotificationPipelineExecuted(typeof(StreamInitiatedNotification<StreamProbeItem>));
        RequireNotificationPipelineExecuted(typeof(StreamCompletedNotification<StreamProbeItem>));
    }

    private async Task RunStreamExceptionHandlingTestAsync(Scenario scenario, CancellationToken cancellationToken)
    {
        var results = new List<ExceptionDemoStreamItem>();
        await foreach (var item in scenario.Cqrs.Stream(new ExceptionDemoStreamRequest(), cancellationToken))
            results.Add(item);

        Require(results is [{ Value: -1 }, { Value: -2 }],
            "ExceptionDemoStreamRequest did not stream the exception handler's fallback items.");
        Require(diagnostics.GetExceptionActionCount(typeof(ExceptionDemoStreamRequest)) == 1,
            "The exception action did not run once for ExceptionDemoStreamRequest.");
        Require(diagnostics.GetExceptionHandlerCount(typeof(ExceptionDemoStreamRequest)) == 1,
            "The exception handler did not run once for ExceptionDemoStreamRequest.");
    }

    private static Task RunDiagnosticsApiTestAsync(Scenario scenario, CancellationToken cancellationToken)
    {
        var cqrsDiagnostics = scenario.Services.GetRequiredService<ICqrsDiagnostics>();

        var pingBinding = cqrsDiagnostics.DescribeRequest(typeof(PingCommand));
        Require(pingBinding.HandlerType == typeof(PingCommandHandler), "PingCommand binding did not report PingCommandHandler.");
        Require(pingBinding.ResponseType == typeof(CommandResult), "PingCommand binding did not report CommandResult response type.");

        var pingCounting = typeof(RequestCountingBehavior<PingCommand, CommandResult>);
        Require(pingBinding.Pipeline.All(b => b.BehaviorType != pingCounting),
            "PingCommand's pipeline still lists RequestCountingBehavior, which it is exempted from.");
        Require(pingBinding.ExemptedPipeline.Any(b => b.BehaviorType == pingCounting),
            "PingCommand's exempted pipeline does not list RequestCountingBehavior.");

        var createUserBinding = cqrsDiagnostics.DescribeRequest(typeof(CreateUserCommand));
        Require(createUserBinding.HandlerType == typeof(CreateUserCommandHandler),
            "CreateUserCommand binding did not report CreateUserCommandHandler.");
        Require(createUserBinding.Pipeline.Any(b => b.BehaviorType == typeof(RequestCountingBehavior<CreateUserCommand, CommandResult>)),
            "CreateUserCommand's pipeline does not list RequestCountingBehavior.");

        var interceptorBinding = cqrsDiagnostics.DescribeRequest(typeof(InterceptorDemoCommand));
        Require(interceptorBinding.HandlerType == typeof(InterceptorDemoCommandHandler),
            "InterceptorDemoCommand binding did not report InterceptorDemoCommandHandler.");
        Require(interceptorBinding.PreHandlers.Any(h => h.AttributeType == typeof(CustomInterceptorAttribute) && h.Priority == 10),
            "InterceptorDemoCommand's diagnostics do not list CustomInterceptorAttribute as a pre-handler.");
        Require(interceptorBinding.PostHandlers.Any(h => h.AttributeType == typeof(CustomInterceptorAttribute) && h.Priority == 10),
            "InterceptorDemoCommand's diagnostics do not list CustomInterceptorAttribute as a post-handler.");

        var streamBinding = cqrsDiagnostics.DescribeRequest(typeof(StreamProbeRequest));
        Require(streamBinding.HandlerType == typeof(StreamProbeRequestHandler),
            "StreamProbeRequest binding did not report StreamProbeRequestHandler.");
        Require(streamBinding.ResponseType == typeof(IAsyncEnumerable<StreamProbeItem>),
            "StreamProbeRequest binding did not report IAsyncEnumerable<StreamProbeItem> response type.");

        RequireSorted(pingBinding.Pipeline, "PingCommand pipeline");
        RequireSorted(pingBinding.ExemptedPipeline, "PingCommand exempted pipeline");
        RequireSorted(createUserBinding.Pipeline, "CreateUserCommand pipeline");
        RequireSorted(createUserBinding.ExemptedPipeline, "CreateUserCommand exempted pipeline");
        RequireSorted(streamBinding.Pipeline, "StreamProbeRequest pipeline");
        RequireSorted(streamBinding.ExemptedPipeline, "StreamProbeRequest exempted pipeline");
        return Task.CompletedTask;

        // Sorted by priority, and by type name within a priority, so the description is the same on every run.
        static void RequireSorted(IReadOnlyList<CqrsPipelineBehaviorBinding> bindings, string name)
        {
            for (var i = 1; i < bindings.Count; i++)
            {
                var (previous, next) = (bindings[i - 1], bindings[i]);
                Require(previous.Priority < next.Priority ||
                        (previous.Priority == next.Priority &&
                         string.CompareOrdinal(previous.BehaviorType.FullName, next.BehaviorType.FullName) <= 0),
                    $"The {name} is not sorted by priority and type name.");
            }
        }
    }
}
