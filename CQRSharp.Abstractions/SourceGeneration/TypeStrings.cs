namespace CQRSharp.Abstractions.SourceGeneration;

/// <summary>
/// Provides constant string values for fully-qualified type names used in source generation.
/// This centralizes type name strings, making the system more robust against refactoring.
/// </summary>
public static class TypeStrings
{
    // Abstractions - Markers
    public const string ICommand = "CQRSharp.Abstractions.Data.Interfaces.Markers.Command.ICommand";
    public const string IQuery = "CQRSharp.Abstractions.Data.Interfaces.Markers.Query.IQuery`1";
    public const string RequestBaseGeneric = "CQRSharp.Abstractions.Data.Interfaces.Markers.Request.RequestBase`1";

    // Abstractions - Handlers
    public const string ICommandHandler1 = "CQRSharp.Abstractions.Data.Interfaces.Handlers.ICommandHandler`1";
    public const string ICommandHandler2 = "CQRSharp.Abstractions.Data.Interfaces.Handlers.ICommandHandler`2";
    public const string IQueryHandler2 = "CQRSharp.Abstractions.Data.Interfaces.Handlers.IQueryHandler`2";
    public const string IQueryHandler3 = "CQRSharp.Abstractions.Data.Interfaces.Handlers.IQueryHandler`3";

    // Abstractions - Notifications
    public const string INotification = "CQRSharp.Abstractions.Data.Interfaces.Notifications.INotification";
    public const string INotificationHandler = "CQRSharp.Abstractions.Data.Interfaces.Notifications.INotificationHandler`1";

    // Abstractions - Attributes
    public const string HandlerTypeAttribute = "CQRSharp.Abstractions.Data.Attributes.Requests.HandlerTypeAttribute";
    public const string IPreHandlerAttribute = "CQRSharp.Abstractions.Data.Attributes.Pipelines.IPreHandlerAttribute";
    public const string IPostHandlerAttribute = "CQRSharp.Abstractions.Data.Attributes.Pipelines.IPostHandlerAttribute";
    public const string PipelineExemptionAttribute = "CQRSharp.Abstractions.Data.Attributes.Pipelines.PipelineExemptionAttribute";
    public const string PipelinePriorityAttribute = "CQRSharp.Abstractions.Data.Attributes.Pipelines.PipelinePriorityAttribute";
    public const string NotificationNameAttribute = "CQRSharp.Abstractions.Data.Attributes.Notifications.NotificationNameAttribute";

    // Abstractions - Context & Models
    public const string RequestContextBase = "CQRSharp.Abstractions.Data.Interfaces.Context.RequestContextBase";
    public const string CommandResult = "CQRSharp.Abstractions.Data.Models.Commands.CommandResult";
    public const string PropertySensitivity = "CQRSharp.Abstractions.Data.Models.Requests.PropertySensitivity";

    // Core
    public const string IPipelineBehavior = "CQRSharp.Core.Pipelines.IPipelineBehavior`2";
    public const string IRequestContextFactory = "CQRSharp.Core.Factories.IRequestContextFactory`1";
}