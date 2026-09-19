using System;
using System.Threading;
using System.Threading.Tasks;
using CQRSharp.Abstractions.Interfaces.Context;
using CQRSharp.Abstractions.Interfaces.Markers.Request;

namespace CQRSharp.Core.Factories;

/// <summary>
///     Base class for a request context factory that hydrates its context from asynchronous sources (a database, an
///     HTTP API, …). CQRSharp calls <see cref="CreateContextAsync" /> once per request, before the pipeline runs, so
///     request-scoped data is loaded at a single awaited point — not blocked on, and not scattered as lazy loads
///     through the handler. Derive from this (instead of implementing <see cref="IRequestContextFactory{TContext}" />
///     directly) when context creation needs I/O. Register it as the <see cref="IRequestContextFactory{TContext}" /> for
///     its context type, exactly like a synchronous factory.
/// </summary>
/// <typeparam name="TContext">The context type this factory produces.</typeparam>
public abstract class AsyncRequestContextFactory<TContext> : IRequestContextFactory<TContext>
    where TContext : IRequestContext
{
    /// <summary>
    ///     Asynchronously creates and hydrates the context for <paramref name="request" />. Load whatever request-scoped
    ///     data the handlers need here — ideally as one batched query/call — then return the populated context.
    /// </summary>
    /// <param name="request">The request that requires a context.</param>
    /// <param name="cancellationToken">A token to cancel the asynchronous creation.</param>
    public abstract ValueTask<TContext> CreateContextAsync(IRequest request, CancellationToken cancellationToken);

    /// <summary>
    ///     Not supported on an async factory — CQRSharp creates its context via <see cref="CreateContextAsync" />.
    ///     Override only if you also need a synchronous creation path.
    /// </summary>
    public virtual TContext CreateContext(IRequest request)
        => throw new NotSupportedException(
            $"'{GetType().Name}' is an async context factory; CQRSharp creates its context via CreateContextAsync. " +
            "Override CreateContext only if you also need synchronous creation.");

    // Bridge the executor's non-generic async call to the typed CreateContextAsync above. Explicit so the typed method
    // remains the single thing a derived factory implements.
    async ValueTask<IRequestContext> IInternalRequestContextFactory.CreateContextAsync(
        IRequest request, CancellationToken cancellationToken)
        => await CreateContextAsync(request, cancellationToken).ConfigureAwait(false);
}
