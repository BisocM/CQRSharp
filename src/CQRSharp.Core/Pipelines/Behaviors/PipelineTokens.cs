namespace CQRSharp.Core.Pipelines;

/// <summary>
///     The token a pipeline continuation (request, stream or notification) runs the rest of the chain with.
/// </summary>
internal static class PipelineTokens
{
    /// <summary>
    ///     <paramref name="passed" /> when the behavior passed a token that can be canceled, otherwise the token the
    ///     behavior itself received. The continuation's parameter is defaulted, so <c>next()</c> arrives here as
    ///     <see cref="CancellationToken.None" />: taken literally it would strip cancellation from everything
    ///     downstream.
    /// </summary>
    public static CancellationToken Flow(CancellationToken passed, CancellationToken received)
        => passed.CanBeCanceled ? passed : received;
}
