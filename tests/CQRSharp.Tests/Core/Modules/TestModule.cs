using CQRSharp.Core.Exceptions;
using CQRSharp.Core.Idempotency;
using CQRSharp.Core.Modules;
using CQRSharp.Core.Notifications;
using CQRSharp.Core.Pipelines;
using CQRSharp.Core.Registries;
using CQRSharp.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace CQRSharp.Tests.Core;

/// <summary>
///     A module built by hand, standing in for a generated one, so the composition's merge rules can be tested with
///     modules whose contents a test chooses (a clash that would break every other test if a real module declared it).
///     Every table is empty unless the test fills it.
/// </summary>
internal sealed class TestModule : ICqrsModule
{
    public IReadOnlyDictionary<Type, RequestMetadata> RequestMetadata { get; init; } = new Dictionary<Type, RequestMetadata>();
    public IReadOnlyDictionary<Type, Delegate> HandlerInvokers { get; init; } = new Dictionary<Type, Delegate>();
    public IReadOnlyDictionary<Type, RequestContextSource> ContextSources { get; init; } = new Dictionary<Type, RequestContextSource>();
    public IReadOnlyDictionary<Type, RequestRoute> RequestRoutes { get; init; } = new Dictionary<Type, RequestRoute>();
    public IReadOnlyDictionary<Type, StreamRoute> StreamRoutes { get; init; } = new Dictionary<Type, StreamRoute>();
    public IReadOnlyList<RequestExceptionHook> ExceptionHooks { get; init; } = [];
    public IReadOnlyDictionary<Type, NotificationRoute> NotificationRoutes { get; init; } = new Dictionary<Type, NotificationRoute>();
    public IReadOnlyList<NotificationSubscription> NotificationSubscriptions { get; init; } = [];
    public IReadOnlyDictionary<Type, Func<INotification, string?>> PartitionKeySelectors { get; init; } = new Dictionary<Type, Func<INotification, string?>>();
    public INotificationSerializer? OutboxSerializer { get; init; }
    public IRequestFingerprinter? RequestFingerprinter { get; init; }

    /// <summary>
    ///     The services the generated bootstrap would register for these modules, in order (the composition root's last),
    ///     composed exactly as it composes them.
    /// </summary>
    public static IServiceCollection Compose(IServiceCollection services, params ICqrsModule[] modules)
    {
        services.AddCqrs();
        foreach (var module in modules)
            services.AddSingleton(module);
        services.AddCqrsModuleComposition();
        return services;
    }
}
