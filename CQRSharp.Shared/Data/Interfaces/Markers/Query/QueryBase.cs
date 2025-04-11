using CQRSharp.Shared.Data.Interfaces.Context;
using CQRSharp.Shared.Data.Interfaces.Markers.Request;

namespace CQRSharp.Shared.Data.Interfaces.Markers.Query;

/// <summary>
///     Base class for queries that can optionally specify a custom TContext.
/// </summary>
/// <typeparam name="TResult">The return type of the query.</typeparam>
/// <typeparam name="TContext">A user-defined context type implementing <see cref="IRequestContext" />.</typeparam>
public abstract class QueryBase<TResult, TContext> : RequestBase<TContext>, IQuery<TResult>
    where TContext : IRequestContext;

/// <summary>
///     Non-generic QueryBase for those who do not need custom context.
///     In this case, the Dispatcher resolves the command context via the in-built DefaultRequestContextFactory.
/// </summary>
public abstract class QueryBase<TResult> : QueryBase<TResult, RequestContextBase>
{
    //Again, just inherits everything above, but we pin the context to the generic context of the lib.
}