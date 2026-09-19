using CQRSharp;

namespace CQRSharp;

/// <summary>
///     Base class for streaming requests that can optionally specify a custom <typeparamref name="TContext" />.
/// </summary>
/// <typeparam name="TItem">The streamed element type.</typeparam>
/// <typeparam name="TContext">A user-defined context type implementing <see cref="IRequestContext" />.</typeparam>
public abstract class StreamRequestBase<TItem, TContext> : RequestBase<TContext>, IStreamRequest<TItem>
    where TContext : IRequestContext;

/// <summary>
///     Non-generic StreamRequestBase for those who do not need custom context.
/// </summary>
/// <typeparam name="TItem">The streamed element type.</typeparam>
public abstract class StreamRequestBase<TItem> : StreamRequestBase<TItem, RequestContextBase>
{
    // Pinned to RequestContextBase by default.
}