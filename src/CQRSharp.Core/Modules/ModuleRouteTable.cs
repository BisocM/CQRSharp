using System.Collections.Frozen;
using CQRSharp.Core.Pipelines;

namespace CQRSharp.Core.Modules;

/// <summary>
///     The application's request and stream routes, built once per service provider from every registered module. Where
///     two modules route the same request type, the last registered wins: the bootstrap registers the composition root's
///     own module last, so the application's handler wins over a referenced library's.
/// </summary>
internal sealed class ModuleRouteTable
{
    public ModuleRouteTable(IEnumerable<ICqrsModule> modules)
    {
        var routes = new Dictionary<Type, RequestRoute>();
        var streams = new Dictionary<Type, StreamRoute>();
        foreach (var module in modules)
        {
            foreach (var route in module.RequestRoutes) routes[route.Key] = route.Value;
            foreach (var route in module.StreamRoutes) streams[route.Key] = route.Value;
        }

        Routes = routes.ToFrozenDictionary();
        StreamRoutes = streams.ToFrozenDictionary();
        RequestTypes = routes.Keys.Concat(streams.Keys).OrderBy(t => t.FullName, StringComparer.Ordinal).ToArray();
    }

    /// <summary>The command and query routes, by exact request type.</summary>
    public FrozenDictionary<Type, RequestRoute> Routes { get; }

    /// <summary>The stream routes, by exact request type.</summary>
    public FrozenDictionary<Type, StreamRoute> StreamRoutes { get; }

    /// <summary>Every routed request type, ordered by name.</summary>
    public Type[] RequestTypes { get; }
}
