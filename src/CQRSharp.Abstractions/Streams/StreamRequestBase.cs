
namespace CQRSharp;

/// <summary>
///     Base class for a streaming request whose handler reads a context of a custom type, built by the
///     <c>IRequestContextFactory&lt;TContext&gt;</c> for that type when the stream is dispatched.
/// </summary>
/// <typeparam name="TItem">The streamed element type.</typeparam>
/// <typeparam name="TContext">A user-defined context type implementing <see cref="IRequestContext" />.</typeparam>
public abstract class StreamRequestBase<TItem, TContext> : RequestBase<TContext>, IStreamRequest<TItem>
    where TContext : IRequestContext;

/// <summary>
///     StreamRequestBase for a streaming request that needs no custom context: its context is a
///     <see cref="RequestContextBase" />, stamped by the dispatcher with the application's <c>TimeProvider</c> time (or
///     created by an <c>IRequestContextFactory&lt;RequestContextBase&gt;</c> the application registers).
/// </summary>
/// <typeparam name="TItem">The streamed element type.</typeparam>
public abstract class StreamRequestBase<TItem> : StreamRequestBase<TItem, RequestContextBase>;