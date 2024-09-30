namespace CQRSharp.Core.Pipeline
{
    /// <summary>
    /// Defines an interface for pipeline behaviors that can be applied globally to all commands.
    /// </summary>
    /// <typeparam name="TRequest">The type of the command.</typeparam>
    /// <typeparam name="TResult">The type of the result returned by the command.</typeparam>
    public interface IPipelineBehavior<TRequest, TResult>
    {
        /// <summary>
        /// Handles the command by invoking the next behavior in the pipeline or the command handler.
        /// </summary>
        /// <param name="request">The command being handled.</param>
        /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
        /// <param name="next">The next delegate to be invoked.</param>
        /// <returns>A task representing the asynchronous operation, containing the result.</returns>
        Task<TResult> Handle(TRequest request, CancellationToken cancellationToken, Func<CancellationToken, Task<TResult>> next);
    }
}