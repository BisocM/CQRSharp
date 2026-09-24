using System.Diagnostics.CodeAnalysis;

namespace CQRSharp.Persistence;

/// <summary>
///     Turns the result of a completed idempotent request into bytes the <see cref="IIdempotencyStore" /> can keep, and
///     back, so a duplicate request is answered with the <em>original</em> result rather than rejected.
/// </summary>
/// <remarks>
///     Optional. A plain <c>CommandResult</c> needs no serializer: the idempotency behavior records and replays it itself
///     (a success as nothing at all, a failure its unit of work committed as a record of its own). A serializer is only
///     needed for value-carrying results (<c>CommandResult&lt;T&gt;</c>, query results). Both methods report failure
///     instead of throwing: a result that cannot be stored or read back is not replayed, and its duplicate is rejected
///     with <c>DuplicateRequestException</c> instead.
/// </remarks>
public interface IIdempotencyResultSerializer
{
    /// <summary>Serializes <paramref name="result" /> for storage.</summary>
    /// <typeparam name="TResult">The request's result type.</typeparam>
    /// <param name="result">The result of the completed request.</param>
    /// <param name="payload">The bytes to store.</param>
    /// <returns><c>false</c> when this serializer cannot handle <typeparamref name="TResult" />.</returns>
    bool TrySerialize<TResult>(TResult result, out byte[] payload);

    /// <summary>Reads back a result stored by <see cref="TrySerialize{TResult}" />.</summary>
    /// <typeparam name="TResult">The request's result type.</typeparam>
    /// <param name="payload">The stored bytes.</param>
    /// <param name="result">The reconstructed result, when the method returns <c>true</c>.</param>
    /// <returns><c>false</c> when the payload cannot be read as <typeparamref name="TResult" />.</returns>
    bool TryDeserialize<TResult>(byte[] payload, [MaybeNullWhen(false)] out TResult result);
}
