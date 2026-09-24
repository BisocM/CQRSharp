using CQRSharp.Core.BackgroundTasks;
using CQRSharp.Core.Diagnostics;
using CQRSharp.Core.Modules;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CQRSharp.Core.Pipelines;

/// <summary>
///     Everything a <see cref="PipelineExecutor" /> needs that is identical for every DI scope, resolved once. The
///     executor itself is scoped — one per web request — so its construction has to be a handful of field copies, not a
///     series of container lookups.
/// </summary>
internal sealed class PipelineExecutorShared(
    IServiceScopeFactory scopeFactory,
    IOptions<DispatcherOptions> dispatcherOptions,
    IOptions<OutboxOptions> outboxOptions,
    IBackgroundTaskManager backgroundTaskManager,
    RequestPlanCache plans,
    ModuleRouteTable routeTable,
    ILogger<PipelineExecutor> logger,
    CqrsMetrics metrics,
    TimeProvider timeProvider,
    ConsumerReadiness consumerReadiness,
    IOptions<BackgroundTaskQueueOptions> queueOptions)
{
    /// <summary>The application's clock: every time read on the dispatch path goes through it.</summary>
    public TimeProvider TimeProvider { get; } = timeProvider;

    /// <summary>Completes once the background queue's consumer runs; a queued dispatch waits for it.</summary>
    public ConsumerReadiness ConsumerReadiness { get; } = consumerReadiness;

    /// <summary>How long a queued dispatch waits for <see cref="ConsumerReadiness" /> before it fails.</summary>
    public TimeSpan ConsumerStartTimeout { get; } = queueOptions.Value.ConsumerStartTimeout;

    /// <summary>Where the executor reports the failures it must not let replace a request's own.</summary>
    public ILogger<PipelineExecutor> Logger { get; } = logger;

    /// <summary>The provider's CQRSharp instruments.</summary>
    public CqrsMetrics Metrics { get; } = metrics;

    /// <summary>The provider-wide route table each scope's dispatcher routes through.</summary>
    public ModuleRouteTable RouteTable { get; } = routeTable;

    public IServiceScopeFactory ScopeFactory { get; } = scopeFactory;
    public IOptions<DispatcherOptions> DispatcherOptions { get; } = dispatcherOptions;
    public IBackgroundTaskManager BackgroundTaskManager { get; } = backgroundTaskManager;
    public RequestPlanCache Plans { get; } = plans;
    public bool OutboxEnabled { get; } = PipelineExecutor.IsOutboxEnabled(outboxOptions);
}
