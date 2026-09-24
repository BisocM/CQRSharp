namespace CQRSharp.Core.Registries;

/// <summary>
///     The source-generated handler invokers, keyed by request type, merged from every module.
/// </summary>
internal interface IHandlerRegistry
{
    /// <summary>
    ///     The typed invoker for <paramref name="requestType" />: a
    ///     <c>Func&lt;object, TRequest, CancellationToken, Task&lt;TResult&gt;&gt;</c> for a command or query, or a
    ///     <c>Func&lt;object, TRequest, CancellationToken, IAsyncEnumerable&lt;TItem&gt;&gt;</c> for a streaming request. It
    ///     returns the handler's own task/stream, so a dispatch neither boxes the result nor adds a state machine.
    /// </summary>
    /// <param name="requestType">The exact request type.</param>
    /// <returns>The invoker, to be cast to its closed delegate type; <c>null</c> when the request has no handler.</returns>
    Delegate? TryGetInvoker(Type requestType);
}
