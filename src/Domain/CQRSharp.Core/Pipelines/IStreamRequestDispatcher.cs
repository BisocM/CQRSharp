using CQRSharp.Abstractions.Interfaces.Markers.Request;
using CQRSharp.Abstractions.Interfaces.Markers.Stream;

namespace CQRSharp.Core.Pipelines;

/// <summary>
///     Defines a unified mechanism for executing streaming requests and consuming their results as <see cref="IAsyncEnumerable{T}" />.
/// </summary>
public interface IStreamRequestDispatcher
{
    /// <summary>
    ///     Executes a streaming request and returns its stream.
    /// </summary>
    IAsyncEnumerable<TItem> ExecuteAsync<TItem>(
        IStreamRequest<TItem> request,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Executes a streaming request by runtime type (untyped), returning the boxed elements.
    /// </summary>
    IAsyncEnumerable<object?> ExecuteAsync(
        IStreamRequest request,
        CancellationToken cancellationToken = default);
}
