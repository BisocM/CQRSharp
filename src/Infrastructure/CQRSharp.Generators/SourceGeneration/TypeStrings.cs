namespace CQRSharp.Generators.SourceGeneration;

/// <summary>
///     Fully-qualified metadata names for CQRSharp.Abstractions types used during source generation.
/// </summary>
/// <remarks>
///     These live in the generator (not in the shipped Abstractions package) so that source-generation plumbing is
///     not part of the public runtime surface. Core type names live alongside in <see cref="CoreTypeStrings" />.
/// </remarks>
internal static class TypeStrings
{
    // Abstractions - Markers
    public const string ICommand = "CQRSharp.Abstractions.Interfaces.Markers.Command.ICommand";
    public const string IQuery = "CQRSharp.Abstractions.Interfaces.Markers.Query.IQuery`1";
    public const string IStreamRequest = "CQRSharp.Abstractions.Interfaces.Markers.Stream.IStreamRequest`1";
    public const string RequestBaseGeneric = "CQRSharp.Abstractions.Interfaces.Markers.Request.RequestBase`1";

    // Abstractions - Handlers
    public const string ICommandHandler1 = "CQRSharp.Abstractions.Interfaces.Handlers.ICommandHandler`1";
    public const string ICommandHandler2 = "CQRSharp.Abstractions.Interfaces.Handlers.ICommandHandler`2";
    public const string IQueryHandler2 = "CQRSharp.Abstractions.Interfaces.Handlers.IQueryHandler`2";
    public const string IQueryHandler3 = "CQRSharp.Abstractions.Interfaces.Handlers.IQueryHandler`3";
    public const string IStreamRequestHandler2 = "CQRSharp.Abstractions.Interfaces.Handlers.IStreamRequestHandler`2";
    public const string IStreamRequestHandler3 = "CQRSharp.Abstractions.Interfaces.Handlers.IStreamRequestHandler`3";

    // Abstractions - Notifications
    public const string INotification = "CQRSharp.Abstractions.Interfaces.Notifications.INotification";
    public const string INotificationHandler = "CQRSharp.Abstractions.Interfaces.Notifications.INotificationHandler`1";

    // Abstractions - Validation
    public const string IRequestValidator1 = "CQRSharp.Abstractions.Interfaces.Validation.IRequestValidator`1";

    // Abstractions - Exception Hooks
    public const string IRequestExceptionHandler3 =
        "CQRSharp.Abstractions.Interfaces.Exceptions.IRequestExceptionHandler`3";

    public const string IRequestExceptionAction2 =
        "CQRSharp.Abstractions.Interfaces.Exceptions.IRequestExceptionAction`2";

    // Abstractions - Attributes
    public const string HandlerTypeAttribute = "CQRSharp.Abstractions.Attributes.Requests.HandlerTypeAttribute";
    public const string IPreHandlerAttribute = "CQRSharp.Abstractions.Attributes.Pipelines.IPreHandlerAttribute";
    public const string IPostHandlerAttribute = "CQRSharp.Abstractions.Attributes.Pipelines.IPostHandlerAttribute";
    public const string PipelineExemptionAttribute = "CQRSharp.Abstractions.Attributes.Pipelines.PipelineExemptionAttribute";
    public const string NotificationNameAttribute = "CQRSharp.Abstractions.Attributes.Notifications.NotificationNameAttribute";

    // Abstractions - Context & Models
    public const string RequestContextBase = "CQRSharp.Abstractions.Interfaces.Context.RequestContextBase";
    public const string CommandResult = "CQRSharp.Abstractions.Models.Commands.CommandResult";
}
