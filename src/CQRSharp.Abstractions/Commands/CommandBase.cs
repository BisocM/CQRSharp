
namespace CQRSharp;

/// <summary>
///     Base class for a command whose handler reads a context of a custom type, built by the
///     <c>IRequestContextFactory&lt;TContext&gt;</c> for that type when the command is dispatched.
/// </summary>
/// <typeparam name="TContext">A user-defined context type implementing <see cref="IRequestContext" />.</typeparam>
public abstract class CommandBase<TContext> : RequestBase<TContext>, ICommand
    where TContext : IRequestContext;

/// <summary>
///     CommandBase for a command that needs no custom context: its context is a <see cref="RequestContextBase" />,
///     stamped by the dispatcher with the application's <c>TimeProvider</c> time (or created by an
///     <c>IRequestContextFactory&lt;RequestContextBase&gt;</c> the application registers).
/// </summary>
public abstract class CommandBase : CommandBase<RequestContextBase>;