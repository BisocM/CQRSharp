using CQRSharp.Abstractions.Interfaces.Context;
using CQRSharp.Abstractions.Interfaces.Markers.Request;

namespace CQRSharp.Abstractions.Interfaces.Markers.Command;

/// <summary>
///     Base class for a value-returning command (<see cref="ICommand{TResult}" />) with a custom context. Derives from
///     <see cref="RequestBase{TContext}" /> rather than <c>CommandBase</c> so it does not also implement the
///     outcome-only <see cref="ICommand" /> (which would give the request two conflicting response types).
/// </summary>
/// <typeparam name="TResult">The value the command returns on success.</typeparam>
/// <typeparam name="TContext">A user-defined context type implementing <see cref="IRequestContext" />.</typeparam>
public abstract class ResultCommandBase<TResult, TContext> : RequestBase<TContext>, ICommand<TResult>
    where TContext : IRequestContext;

/// <summary>
///     Base class for a value-returning command using the default <see cref="RequestContextBase" /> context.
/// </summary>
/// <typeparam name="TResult">The value the command returns on success.</typeparam>
public abstract class ResultCommandBase<TResult> : ResultCommandBase<TResult, RequestContextBase>;
