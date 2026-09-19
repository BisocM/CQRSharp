using CQRSharp.Core.Background.TaskQueue;
using CQRSharp.Core.Caching.Contexts;
using CQRSharp.Core.Caching.Handlers;
using CQRSharp.Core.Caching.Requests;
using CQRSharp.Core.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CQRSharp.Core.Pipelines;

/// <summary>
///     Everything a <see cref="PipelineExecutor" /> needs that is identical for every DI scope, resolved once. The
///     executor itself is scoped — one per web request — so its construction has to be a handful of field copies, not a
///     series of container lookups.
/// </summary>
internal sealed class PipelineExecutorShared(
    IServiceScopeFactory scopeFactory,
    IRequestRegistry requestRegistry,
    IHandlerRegistry handlerRegistry,
    IContextFactoryRegistry contextFactoryRegistry,
    IOptions<DispatcherOptions> dispatcherOptions,
    IOptions<OutboxOptions> outboxOptions,
    IBackgroundTaskManager backgroundTaskManager,
    RequestPlanCache plans,
    IServiceProvider rootProvider)
{
    private Modules.ModuleRouteTable? _routeTable;

    /// <summary>
    ///     The provider-wide route table, handed to the request dispatcher along with the executor so that building a
    ///     dispatcher for a new scope does not need a container lookup of its own. Resolved lazily: it is registered by the
    ///     module composition, which a bare AddCqrs() without generated modules does not run.
    /// </summary>
    public Modules.ModuleRouteTable? RouteTable => _routeTable ??= rootProvider.GetService<Modules.ModuleRouteTable>();

    public IServiceScopeFactory ScopeFactory { get; } = scopeFactory;
    public IRequestRegistry RequestRegistry { get; } = requestRegistry;
    public IHandlerRegistry HandlerRegistry { get; } = handlerRegistry;
    public IContextFactoryRegistry ContextFactoryRegistry { get; } = contextFactoryRegistry;
    public IOptions<DispatcherOptions> DispatcherOptions { get; } = dispatcherOptions;
    public IBackgroundTaskManager BackgroundTaskManager { get; } = backgroundTaskManager;
    public RequestPlanCache Plans { get; } = plans;
    public bool OutboxEnabled { get; } = PipelineExecutor.IsOutboxEnabled(outboxOptions);
}
