using CQRSharp.Pipelines;
using CQRSharp.Core.Background.TaskQueue;
using CQRSharp.Core.Caching.Contexts;
using CQRSharp.Core.Caching.Handlers;
using CQRSharp.Core.Caching.Requests;
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
    IServiceProvider rootProvider,
    TimeProvider? timeProvider = null)
{
    /// <summary>The application's clock: every time read on the dispatch path goes through it.</summary>
    public TimeProvider TimeProvider { get; } = timeProvider ?? TimeProvider.System;

    private Modules.ModuleRouteTable? _routeTable;

    /// <summary>
    ///     The provider-wide route table, handed to the request dispatcher along with the executor so that building a
    ///     dispatcher for a new scope does not need a container lookup of its own. Resolved lazily: it is registered by the
    ///     module composition, which a bare AddCqrs() without generated modules does not run.
    /// </summary>
    public Modules.ModuleRouteTable? RouteTable => _routeTable ??= rootProvider.GetService<Modules.ModuleRouteTable>();

    private bool? _usesDefaultRequestWiring;

    /// <summary>
    ///     Whether <see cref="IRequestDispatcher" /> and <see cref="IPipelineExecutor" /> are the built-in registrations.
    ///     When they are, the façade builds the pair directly from this singleton — one container lookup per scope instead
    ///     of three. Probed once, from a throwaway scope; any replacement (a decorator, a test double) turns it off and the
    ///     façade goes back to resolving through the container, so a custom registration is always honoured.
    /// </summary>
    public bool UsesDefaultRequestWiring
    {
        get
        {
            if (_usesDefaultRequestWiring is { } known) return known;

            bool isDefault;
            try
            {
                using var scope = ScopeFactory.CreateScope();
                isDefault = RouteTable is not null &&
                            scope.ServiceProvider.GetService<IRequestDispatcher>()
                                is Modules.CompositeRequestDispatcher { Executor: PipelineExecutor { Shared: not null } };
            }
            catch
            {
                isDefault = false;
            }

            _usesDefaultRequestWiring = isDefault;
            return isDefault;
        }
    }

    public IServiceScopeFactory ScopeFactory { get; } = scopeFactory;
    public IRequestRegistry RequestRegistry { get; } = requestRegistry;
    public IHandlerRegistry HandlerRegistry { get; } = handlerRegistry;
    public IContextFactoryRegistry ContextFactoryRegistry { get; } = contextFactoryRegistry;
    public IOptions<DispatcherOptions> DispatcherOptions { get; } = dispatcherOptions;
    public IBackgroundTaskManager BackgroundTaskManager { get; } = backgroundTaskManager;
    public RequestPlanCache Plans { get; } = plans;
    public bool OutboxEnabled { get; } = PipelineExecutor.IsOutboxEnabled(outboxOptions);
}
