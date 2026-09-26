
namespace CQRSharp;

/// <summary>
///     Base class for a query whose handler reads a context of a custom type, built by the
///     <c>IRequestContextFactory&lt;TContext&gt;</c> for that type when the query is dispatched.
/// </summary>
/// <typeparam name="TResult">The return type of the query.</typeparam>
/// <typeparam name="TContext">A user-defined context type implementing <see cref="IRequestContext" />.</typeparam>
public abstract class QueryBase<TResult, TContext> : RequestBase<TContext>, IQuery<TResult>
    where TContext : IRequestContext;

/// <summary>
///     QueryBase for a query that needs no custom context: its context is a <see cref="RequestContextBase" />, stamped
///     by the dispatcher with the application's <c>TimeProvider</c> time (or created by an
///     <c>IRequestContextFactory&lt;RequestContextBase&gt;</c> the application registers).
/// </summary>
/// <typeparam name="TResult">The return type of the query.</typeparam>
public abstract class QueryBase<TResult> : QueryBase<TResult, RequestContextBase>;