using CQRSharp.Core.Pipeline;

namespace CQRSharpSample.Behaviors
{
    public class LoggingBehavior<TCommand, TResult> : IPipelineBehavior<TCommand, TResult>
    {
        public async Task<TResult> Handle(TCommand command, CancellationToken cancellationToken, Func<Task<TResult>> next)
        {
            Console.WriteLine($"[PIPELINE] Handling command of type {typeof(TCommand).Name}");

            var result = await next();

            Console.WriteLine($"[PIPELINE] Handled command of type {typeof(TCommand).Name}");

            return result;
        }
    }

}
