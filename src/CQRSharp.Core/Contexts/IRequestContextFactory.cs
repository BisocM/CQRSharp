namespace CQRSharp;

/// <summary>
///     Creates the context of every request whose context type is <typeparamref name="TContext" /> (the <c>TContext</c> of
///     its <c>CommandBase&lt;TContext&gt;</c>, <c>QueryBase&lt;TResult, TContext&gt;</c>, …). The dispatcher calls it once
///     per request, before any behavior or the handler runs, and puts the result on <see cref="IRequest.Context" />,
///     replacing whatever the request arrived with. A factory is therefore where request-scoped identity comes from: read
///     the user or the tenant from a trusted ambient source (the current HTTP user, a scoped service), never from the
///     request itself.
/// </summary>
/// <remarks>
///     <para>
///         The factory builds the context of the caller: it is resolved from the scope of the dispatcher the request is
///         sent through and runs on the flow that sends it, before the request is handed to the background queue
///         (<see cref="RunMode.Queued" />) or given a scope of its own (<see cref="ExecutionScopeMode.New" />). Its scoped
///         dependencies are therefore the caller's instances, and the ambient state it reads (the current HTTP user
///         through <c>IHttpContextAccessor</c>, any <see cref="AsyncLocal{T}" />) is the caller's, whichever scope and
///         thread the handler then runs on. A request a handler sends is sent by that handler, so its context comes from
///         the handler's scope. A request sent through a dispatcher whose scope has already ended cannot have its context
///         built, since the factory cannot be resolved from that scope; only the built-in context of
///         <see cref="RequestContextBase" />, which is built without resolving anything, can.
///     </para>
///     <para>
///         The source generator registers every non-generic factory it finds as a transient, so declaring one is enough.
///         A context type has one factory. A factory registered by hand (a lambda, or a type in an assembly the generator
///         does not run in) takes precedence over a discovered one, whether it is registered before or after
///         <c>AddCqrsGenerated</c>. Among discovered factories, the one of the assembly that calls <c>AddCqrsGenerated</c>
///         replaces a referenced assembly's, as its handler does for a request both handle; two factories for one context
///         type in one assembly are an error (CQRGEN018), and two in referenced assemblies with none in the composing one
///         are reported where it composes them (CQRGEN019). The contexts of <see cref="RequestContextBase" /> come from a
///         built-in factory that stamps the application's <c>TimeProvider</c> time; an
///         <c>IRequestContextFactory&lt;RequestContextBase&gt;</c> of your own replaces it.
///     </para>
/// </remarks>
/// <typeparam name="TContext">The context type the factory creates.</typeparam>
public interface IRequestContextFactory<TContext> where TContext : IRequestContext
{
    /// <summary>
    ///     Creates the context for <paramref name="request" />, loading whatever request-scoped data the handlers need at
    ///     this one awaited point rather than through lazy loads in the handler. A factory that needs no I/O returns its
    ///     context synchronously: <c>new ValueTask&lt;TContext&gt;(context)</c>. A context built with the parameterless
    ///     <c>RequestContextBase()</c> constructor is stamped from the application's <c>TimeProvider</c>.
    /// </summary>
    /// <param name="request">The request being dispatched.</param>
    /// <param name="cancellationToken">The dispatch's cancellation token.</param>
    /// <returns>The context; never <see langword="null" />.</returns>
    ValueTask<TContext> CreateContextAsync(IRequest request, CancellationToken cancellationToken);
}
