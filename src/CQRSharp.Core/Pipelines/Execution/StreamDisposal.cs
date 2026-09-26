namespace CQRSharp.Core.Pipelines;

/// <summary>
///     How every layer of a stream pipeline (the executor and each stream behavior) disposes the stream it wraps once
///     that stream ran to its end or failed, so they all answer a failing disposal the same way.
/// </summary>
internal static class StreamDisposal
{
    /// <summary>
    ///     Disposes <paramref name="enumerator" /> and returns the failure the stream ended with. A stream whose disposal
    ///     fails did not complete, so that failure becomes the stream's; unless the stream had already failed, whose own
    ///     failure stays the one its consumer receives, and the disposal's is handed back as <c>Suppressed</c> for the
    ///     caller to log.
    /// </summary>
    public static async ValueTask<(Exception? Failure, Exception? Suppressed)> DisposeAsync<TItem>(
        IAsyncEnumerator<TItem> enumerator,
        Exception? failure)
    {
        try
        {
            await enumerator.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return failure is null ? (ex, null) : (failure, ex);
        }

        return (failure, null);
    }
}
