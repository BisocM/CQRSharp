
namespace CQRSharp;

/// <summary>
///     Marks a request type dispatched through <c>ICqrsDispatcher</c>. <c>ICommand</c>, <c>IQuery&lt;TResult&gt;</c> and
///     <c>IStreamRequest&lt;TItem&gt;</c> derive from it.
/// </summary>
/// <remarks>
///     A request instance is single-use. The dispatcher writes <see cref="Context" /> onto the instance before the
///     pipeline runs, so the same instance must not be dispatched more than once or shared across concurrent dispatches;
///     construct a new request object per dispatch.
/// </remarks>
public interface IRequest
{
    /// <summary>
    ///     The request's context: who and what the request runs for (a user, a tenant) and when it was created. The
    ///     dispatcher sets it from the context type's <c>IRequestContextFactory&lt;TContext&gt;</c> before any behavior or
    ///     the handler runs, replacing whatever was set before the dispatch, so the value a handler reads always comes from
    ///     the registered factory and never from the request's sender. Setting it directly is for code that runs a
    ///     handler or behavior without the dispatcher, such as a unit test.
    /// </summary>
    public IRequestContext? Context { get; set; }
}

/// <summary>
///     Marker interface for a request that produces a response of type <typeparamref name="TResponse" /> when dispatched.
/// </summary>
/// <typeparam name="TResponse">The type of the response returned by handling the request.</typeparam>
public interface IRequest<out TResponse> : IRequest
{
}
