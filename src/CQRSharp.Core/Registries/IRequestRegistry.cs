using System.Diagnostics.CodeAnalysis;

namespace CQRSharp.Core.Registries;

/// <summary>
///     The <see cref="RequestMetadata" /> of every request the application handles, merged from every source-generated
///     module.
/// </summary>
internal interface IRequestRegistry
{
    /// <summary>
    ///     The metadata of <paramref name="requestType" />.
    /// </summary>
    /// <param name="requestType">The exact request type.</param>
    /// <param name="metadata">The metadata, when the request is handled.</param>
    /// <returns><see langword="true" /> when the request is handled.</returns>
    bool TryGetRequestMetadata(Type requestType, [NotNullWhen(true)] out RequestMetadata? metadata);
}
