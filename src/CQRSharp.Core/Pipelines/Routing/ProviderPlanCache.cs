using System.Collections.Concurrent;

namespace CQRSharp.Core.Pipelines;

/// <summary>
///     A plan computed once per service provider for one closed generic type (a request, a stream, a notification): what
///     the container can answer about its registrations once it is built, so a dispatch does not ask it again.
/// </summary>
internal abstract class ProviderPlan
{
    /// <summary>The cache that built this plan; a static slot's plan is only used by the cache that owns it.</summary>
    public required ProviderPlanCache Owner { get; init; }
}

/// <summary>
///     The plans of one service provider, one per plan type. The steady state is a static slot per plan type holding the
///     last provider's plan, so finding it is a reference comparison rather than a dictionary lookup; a process with
///     several providers (test hosts, several hosts in one process) falls back to this cache's dictionary whenever the
///     slot holds another provider's plan, and every provider still gets its own.
/// </summary>
internal sealed class ProviderPlanCache : IDisposable
{
    private readonly ConcurrentDictionary<Type, ProviderPlan> _plans = new();

    // One reset per static slot this cache ever filled, so disposing the provider (a test host, an in-process restart)
    // does not leave its plans - and through them the provider's whole graph - rooted by a static for the process life.
    private readonly ConcurrentBag<Action> _slotResets = new();

    /// <summary>The plan of type <typeparamref name="TPlan" />, built by <paramref name="build" /> on first use.</summary>
    /// <param name="state">What <paramref name="build" /> builds from; passed through so the builder can be a static lambda.</param>
    /// <param name="build">Builds the plan; it must set <see cref="ProviderPlan.Owner" /> to the cache it is given.</param>
    public TPlan Get<TPlan, TState>(TState state, Func<ProviderPlanCache, TState, TPlan> build) where TPlan : ProviderPlan
    {
        var cached = Slot<TPlan>.Plan;
        return cached is not null && ReferenceEquals(cached.Owner, this) ? cached : GetOrBuild(state, build);
    }

    public void Dispose()
    {
        foreach (var reset in _slotResets) reset();
        _plans.Clear();
    }

    private TPlan GetOrBuild<TPlan, TState>(TState state, Func<ProviderPlanCache, TState, TPlan> build) where TPlan : ProviderPlan
    {
        var plan = (TPlan)_plans.GetOrAdd(
            typeof(TPlan),
            static (_, args) =>
            {
                var built = args.build(args.self, args.state);
                // Registered once the plan exists: a build that throws (a misconfigured registry, a behavior generated
                // code could not close) is retried on the next dispatch and must not leave a reset behind per attempt.
                args.self._slotResets.Add(() => Slot<TPlan>.Forget(args.self));
                return built;
            },
            (self: this, state, build));

        Slot<TPlan>.Plan = plan;
        return plan;
    }

    private static class Slot<TPlan> where TPlan : ProviderPlan
    {
        public static volatile TPlan? Plan;

        public static void Forget(ProviderPlanCache owner)
        {
            if (Plan is { } plan && ReferenceEquals(plan.Owner, owner)) Plan = null;
        }
    }
}
