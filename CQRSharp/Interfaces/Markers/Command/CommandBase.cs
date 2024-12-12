using CQRSharp.Data.Context;
using CQRSharp.Interfaces.Markers.Request;

namespace CQRSharp.Interfaces.Markers.Command;

/// <summary>
///     Base class for commands.
/// </summary>
public abstract class CommandBase<TContext> : RequestBase, ICommand
    where TContext : IRequestContext
{
    /// <summary>
    /// A strongly typed context property. In the library’s runtime usage, 
    /// this will be assigned automatically by the dispatcher or context factory.
    /// </summary>
    public new TContext Context
    {
        get => (TContext)base.Context!;
        set => base.Context = value;
    }
}

/// <summary>
/// Non-generic CommandBase for those who do not need custom context.
/// </summary>
public abstract class CommandBase : CommandBase<RequestContextBase>
{
    //Inherits everything from above, just pinned to RequestContextBase by default.
}