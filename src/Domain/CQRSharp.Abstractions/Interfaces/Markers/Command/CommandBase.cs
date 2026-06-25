using CQRSharp.Abstractions.Interfaces.Context;
using CQRSharp.Abstractions.Interfaces.Markers.Request;

namespace CQRSharp.Abstractions.Interfaces.Markers.Command;

/// <summary>
///     Base class for commands that can optionally specify a custom TContext.
/// </summary>
public abstract class CommandBase<TContext> : RequestBase<TContext>, ICommand
    where TContext : IRequestContext;

/// <summary>
///     Non-generic CommandBase for those who do not need custom context.
///     In this case, the Dispatcher resolves the command context via the in-built DefaultRequestContextFactory.
/// </summary>
public abstract class CommandBase : CommandBase<RequestContextBase>
{
    //Inherits everything from above, just pinned to RequestContextBase by default.
}