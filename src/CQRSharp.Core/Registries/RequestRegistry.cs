using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;

namespace CQRSharp.Core.Registries;

/// <summary>The merged <see cref="IRequestRegistry" /> the module composition builds.</summary>
internal sealed class RequestRegistry(FrozenDictionary<Type, RequestMetadata> metadata) : IRequestRegistry
{
    public bool TryGetRequestMetadata(Type requestType, [NotNullWhen(true)] out RequestMetadata? requestMetadata)
        => metadata.TryGetValue(requestType, out requestMetadata);
}
