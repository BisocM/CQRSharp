namespace CQRSharp.Core.Pipelines;

/// <summary>Which lifecycle a command or query dispatch follows: its notifications, its span name and its metric.</summary>
internal enum RequestKind
{
    /// <summary>A command: <c>Command*</c> notifications, which carry its result as a <see cref="CommandResult" />.</summary>
    Command,

    /// <summary>A query: <c>Query*</c> notifications.</summary>
    Query,

    /// <summary>Any other request: no lifecycle notifications.</summary>
    Other
}

/// <summary>The <see cref="RequestKind" /> of a request type dispatched with a result type, decided once per pair.</summary>
internal static class RequestKindOf<TRequest, TResult>
{
    // The command marker alone does not make a command: any request type can implement ICommandMarker, and the Command*
    // notifications hand the result on as a CommandResult. A command is a marked request dispatched with a CommandResult
    // (ICommand) or a CommandResult<T> (ICommand<T>); anything else is classified by what it is otherwise.
    public static readonly RequestKind Value =
        typeof(ICommandMarker).IsAssignableFrom(typeof(TRequest)) && typeof(CommandResult).IsAssignableFrom(typeof(TResult))
            ? RequestKind.Command
            : typeof(IQuery<TResult>).IsAssignableFrom(typeof(TRequest))
                ? RequestKind.Query
                : RequestKind.Other;
}
