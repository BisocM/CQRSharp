using CQRSharp.Interfaces.Markers;

namespace CQRSharp.Interfaces.Handlers
{
    /// <summary>
    /// Interface for handling commands that return a result of type <typeparamref name="TResult"/>.
    /// </summary>
    /// <typeparam name="TCommand">The type of the command.</typeparam>
    /// <typeparam name="TResult">The type of the result returned by the command.</typeparam>
    public interface IQueryHandler<in TCommand, TResult> where TCommand : IQuery<TResult>
    {
        Task<TResult> Handle(TCommand command, CancellationToken cancellationToken);
    }
}