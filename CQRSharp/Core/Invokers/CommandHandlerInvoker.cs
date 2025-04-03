using System.Collections.Concurrent;
using System.Linq.Expressions;
using CQRSharp.Interfaces.Context;
using CQRSharp.Interfaces.Handlers;

namespace CQRSharp.Core.Invokers;

public static class CommandHandlerInvoker
{
    // Cache delegates for each command type.
    private static readonly ConcurrentDictionary<Type, Func<object, object, CancellationToken, Task>> CommandDelegateCache 
        = new();

    public static Task Handle(object command, object handler, CancellationToken cancellationToken)
    {
        if (command == null)
            throw new ArgumentNullException(nameof(command));
        if (handler == null)
            throw new ArgumentNullException(nameof(handler));

        // Get the command's actual type.
        var commandType = command.GetType();

        // Retrieve or compile the delegate.
        var invoker = CommandDelegateCache.GetOrAdd(commandType, ct =>
        {
            // Build the interface type: ICommandHandler<TCommand>
            var interfaceType = handler.GetType().GetInterfaces()
                .FirstOrDefault(i =>
                    i.IsGenericType &&
                    i.GetGenericTypeDefinition() == typeof(ICommandHandler<,>) &&
                    i.GenericTypeArguments[0] == commandType);

            // Parameter expressions: (object command, object handler, CancellationToken token)
            var commandParam = Expression.Parameter(typeof(object), "command");
            var handlerParam = Expression.Parameter(typeof(object), "handler");
            var tokenParam = Expression.Parameter(typeof(CancellationToken), "token");

            // Convert parameters to the proper types.
            var typedCommand = Expression.Convert(commandParam, commandType);
            var typedHandler = Expression.Convert(handlerParam, interfaceType);

            Console.WriteLine("CALLING HANDLER!!!!!!!!!!!!!!!!!!!!!!");
            
            // Get the Handle method from the interface.
            try
            {
                var methodInfo = interfaceType.GetMethod("Handle")
                                 ?? throw new InvalidOperationException($"Handle method not found on {interfaceType.Name}");
                
                Console.WriteLine("CALLED HANDLER!!!!!!!!!!!!!!!!!!!!!!");
            
                // Create a call expression: handler.Handle(command, token)
                var callExpression = Expression.Call(typedHandler, methodInfo, typedCommand, tokenParam);

                // Create lambda: (object command, object handler, CancellationToken token) => handler.Handle((TCommand)command, token)
                var lambda = Expression.Lambda<Func<object, object, CancellationToken, Task>>(callExpression, commandParam, handlerParam, tokenParam);
                return lambda.Compile();
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
                throw;
            }
        });

        return invoker(command, handler, cancellationToken);
    }
}