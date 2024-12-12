using CQRSharp.Data.Context;
using CQRSharp.Interfaces.Markers.Request;

namespace CQRSharp.Interfaces.Markers.Query;

/// <summary>
///     Base class for queries.
/// </summary>
/// <typeparam name="TResult">The return type of the query.</typeparam>
/// <typeparam name="TContext">A user-defined context type implementing <see cref="IRequestContext"/>.</typeparam>
public abstract class QueryBase<TResult, TContext> 
    : RequestBase, IQuery<TResult>
    where TContext : IRequestContext
{
    /// <summary>
    /// Strongly typed context property, set by the dispatcher or context factory.
    /// </summary>
    public new TContext? Context
    {
        get => (TContext?)base.Context;
        set => base.Context = value;
    }
}

/// <summary>
/// Non-generic QueryBase for those who do not need custom context.
/// </summary>
public abstract class QueryBase<TResult> : QueryBase<TResult, RequestContextBase>
{
    //Again, just inherits everything above, but we pin the context to the generic context of the lib.
}