using System.Collections.Concurrent;
using System.Linq.Expressions;
using CQRSharp.Interfaces.Handlers;

namespace CQRSharp.Core.Invokers;

public static class QueryHandlerInvoker
{
    //Use a composite key of (queryType, resultType).
    private static readonly ConcurrentDictionary<(Type queryType, Type resultType), Delegate> QueryDelegateCache 
        = new();

    public static Task<TResult> Handle<TResult>(object query, object handler, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(handler);

        var queryType = query.GetType();
        var resultType = typeof(TResult);
        var key = (queryType, resultType);

        //Retrieve or compile the delegate.
        var invoker = (Func<object, object, CancellationToken, Task<TResult>>)QueryDelegateCache.GetOrAdd(key, _ =>
        {
            //Build the interface type: IQueryHandler<TQuery, TResult>
            var interfaceType = typeof(IQueryHandler<,>).MakeGenericType(queryType, resultType);

            //Parameter expressions: (object query, object handler, CancellationToken token)
            var queryParam = Expression.Parameter(typeof(object), "query");
            var handlerParam = Expression.Parameter(typeof(object), "handler");
            var tokenParam = Expression.Parameter(typeof(CancellationToken), "token");

            //Convert parameters to proper types.
            var typedQuery = Expression.Convert(queryParam, queryType);
            var typedHandler = Expression.Convert(handlerParam, interfaceType);

            //Get the Handle method from the interface.
            var methodInfo = interfaceType.GetMethod("Handle")
                             ?? throw new InvalidOperationException($"Handle method not found on {interfaceType.Name}");

            //Create a call expression: handler.Handle(query, token)
            var callExpression = Expression.Call(typedHandler, methodInfo, typedQuery, tokenParam);

            //Create lambda: (object query, object handler, CancellationToken token) => handler.Handle((TQuery)query, token)
            var lambda = Expression.Lambda<Func<object, object, CancellationToken, Task<TResult>>>(callExpression, queryParam, handlerParam, tokenParam);
            return lambda.Compile();
        });

        return invoker(query, handler, cancellationToken);
    }
}