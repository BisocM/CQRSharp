
namespace CQRSharp;

/// <summary>
///     The base of every request that carries a typed context: <c>CommandBase</c>, <c>QueryBase</c>,
///     <c>ResultCommandBase</c> and <c>StreamRequestBase</c> derive from it.
/// </summary>
/// <typeparam name="TContext">The context type the request's handler reads.</typeparam>
public abstract class RequestBase<TContext> : IRequest where TContext : IRequestContext
{
    /// <summary>
    ///     The request's context, built by the <c>IRequestContextFactory&lt;TContext&gt;</c> for its type when the request
    ///     is dispatched. It is read-only here so a model binder or a deserializer, which only write public setters,
    ///     cannot supply it: the dispatcher always replaces it with the factory's context before the pipeline runs.
    ///     Outside a dispatch (a handler unit test) set it through <see cref="IRequest.Context" />.
    /// </summary>
    public TContext? Context { get; private set; }

    IRequestContext? IRequest.Context
    {
        get => Context;
        set => Context = value switch
        {
            null => default,
            TContext typed => typed,
            _ => throw new ArgumentException(
                $"A {GetType().Name} takes a context of type '{typeof(TContext).FullName}', not '{value.GetType().FullName}'.",
                nameof(value))
        };
    }
}
