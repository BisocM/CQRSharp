using CQRSharp.Interfaces.Markers.Request;

namespace CQRSharp.Interfaces.Markers.Query
{
    /// <summary>
    /// Base class for queries.
    /// </summary>
    /// <typeparam name="TResult">The return type of the query.</typeparam>
    public abstract class QueryBase<TResult> : RequestBase, IQuery<TResult> { }
}