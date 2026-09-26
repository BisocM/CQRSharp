using Microsoft.Extensions.DependencyInjection;

namespace CQRSharp.Core.Pipelines;

/// <summary>
///     The questions a plan asks a built service provider about its registrations. Their answers cannot change once the
///     provider is built, which is what lets a plan ask them once instead of on every dispatch.
/// </summary>
/// <param name="root">The provider's root.</param>
internal sealed class ProviderRegistrations(IServiceProvider root)
{
    // Null when the container does not expose registration queries (a non-Microsoft container, a hand-built test
    // provider). Every "can this be skipped?" question then answers "no": everything runs, which is always correct.
    private readonly IServiceProviderIsService? _isService = root.GetService<IServiceProviderIsService>();
    private bool? _closesValueTypeBehaviors;

    /// <summary>Whether the container can prove a service is not registered at all.</summary>
    public bool CanProveAbsence => _isService is not null;

    /// <summary><c>false</c> only when the container proves nothing is registered as <paramref name="serviceType" />.</summary>
    public bool MayBeRegistered(Type serviceType) => _isService is null || _isService.IsService(serviceType);

    /// <summary>
    ///     Where the behaviors of one closed behavior interface come from, and whether there can be any: the closed,
    ///     keyed set the module composition registered when <typeparamref name="TValue" /> is a value type on a runtime
    ///     that cannot close open generics over one, the container's own registrations otherwise, merged with the closed
    ///     behaviors the generator discovered when there may be some.
    /// </summary>
    /// <typeparam name="TValue">The request's result, the stream's item, or the notification.</typeparam>
    /// <param name="behaviorService">The closed behavior interface.</param>
    public BehaviorRegistrations Behaviors<TValue>(Type behaviorService)
    {
        var discovered = PipelineBehaviors.MayHaveDiscovered(root, behaviorService);
        var closed = UsesClosedBehaviors<TValue>();
        var mayHaveAny = discovered || (closed
            ? root.GetService<ClosedBehaviorSet>()?.Has(behaviorService) ?? false
            : MayBeRegistered(behaviorService));
        return new BehaviorRegistrations(mayHaveAny, discovered, closed);
    }

    private bool UsesClosedBehaviors<TValue>()
        => typeof(TValue).IsValueType &&
           (_closesValueTypeBehaviors ??= root.GetService<ClosedBehaviorResolution>()?.Enabled ?? false);
}

/// <summary>What a plan knows about the behaviors of one closed behavior interface.</summary>
/// <param name="MayHaveAny"><c>false</c> only when the provider proves no behavior is registered, so resolving them is skipped.</param>
/// <param name="MergesDiscovered">
///     Closed behaviors the generator discovered may be registered under the discovered-services key, so they are merged
///     with the application's own registrations.
/// </param>
/// <param name="UsesClosedSet">
///     The behaviors come from the closed, keyed set the module composition registered (a value type on a runtime without
///     dynamic code) rather than from the open-generic registrations.
/// </param>
internal readonly record struct BehaviorRegistrations(bool MayHaveAny, bool MergesDiscovered, bool UsesClosedSet);
