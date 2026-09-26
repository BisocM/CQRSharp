using System.Collections.Frozen;

namespace CQRSharp.Core.Registries;

/// <summary>The merged <see cref="IContextFactoryRegistry" /> the module composition builds.</summary>
internal sealed class ContextFactoryRegistry(FrozenDictionary<Type, RequestContextSource> sources) : IContextFactoryRegistry
{
    public RequestContextSource? TryGetSource(Type contextType) => sources.GetValueOrDefault(contextType);
}
