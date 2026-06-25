namespace CQRSharp.Abstractions.Attributes.SourceGeneration;

/// <summary>
///     The set of CQRSharp framework "well-known" types that tooling (the source generator and analyzers) resolves
///     via <see cref="CqrsWellKnownTypeAttribute" />. Each member maps to exactly one framework type, declared once
///     with <c>typeof(...)</c> in <c>CQRSharp.Core</c>.
/// </summary>
/// <remarks>
///     Tooling matches these by member <em>name</em>, not by numeric value, so the values may be reordered freely.
///     A guard test asserts this enum stays in sync with the resolver's internal copy.
/// </remarks>
public enum CqrsRole
{
    /// <summary><c>ICqrsDispatcher</c>.</summary>
    Dispatcher,

    /// <summary>The non-generic <c>IRequest</c> marker.</summary>
    IRequest,

    /// <summary><c>INotification</c>.</summary>
    INotification,

    /// <summary><c>ICommand</c>.</summary>
    ICommand,

    /// <summary><c>IQuery&lt;TResult&gt;</c>.</summary>
    IQuery,

    /// <summary><c>IStreamRequest&lt;TItem&gt;</c>.</summary>
    IStreamRequest,

    /// <summary>The non-generic <c>IStreamRequest</c> marker.</summary>
    IStreamRequestMarker,

    /// <summary><c>RequestBase&lt;TContext&gt;</c>.</summary>
    RequestBaseGeneric,

    /// <summary><c>ICommandHandler&lt;TCommand&gt;</c>.</summary>
    ICommandHandler1,

    /// <summary><c>ICommandHandler&lt;TCommand, TContext&gt;</c>.</summary>
    ICommandHandler2,

    /// <summary><c>IQueryHandler&lt;TQuery, TResult&gt;</c>.</summary>
    IQueryHandler2,

    /// <summary><c>IQueryHandler&lt;TQuery, TResult, TContext&gt;</c>.</summary>
    IQueryHandler3,

    /// <summary><c>IStreamRequestHandler&lt;TRequest, TItem&gt;</c>.</summary>
    IStreamRequestHandler2,

    /// <summary><c>IStreamRequestHandler&lt;TRequest, TItem, TContext&gt;</c>.</summary>
    IStreamRequestHandler3,

    /// <summary><c>INotificationHandler&lt;TNotification&gt;</c>.</summary>
    INotificationHandler,

    /// <summary><c>IRequestValidator&lt;TRequest&gt;</c>.</summary>
    IRequestValidator1,

    /// <summary><c>IRequestExceptionHandler&lt;TRequest, TResult, TException&gt;</c>.</summary>
    IRequestExceptionHandler3,

    /// <summary><c>IRequestExceptionAction&lt;TRequest, TException&gt;</c>.</summary>
    IRequestExceptionAction2,

    /// <summary><c>IPreHandlerAttribute</c>.</summary>
    IPreHandlerAttribute,

    /// <summary><c>IPostHandlerAttribute</c>.</summary>
    IPostHandlerAttribute,

    /// <summary><c>PipelineExemptionAttribute</c>.</summary>
    PipelineExemptionAttribute,

    /// <summary><c>NotificationNameAttribute</c>.</summary>
    NotificationNameAttribute,

    /// <summary><c>RequestContextBase</c>.</summary>
    RequestContextBase,

    /// <summary><c>CommandResult</c>.</summary>
    CommandResult,

    /// <summary><c>IPipelineBehavior&lt;TRequest, TResult&gt;</c>.</summary>
    IPipelineBehavior,

    /// <summary><c>IStreamPipelineBehavior&lt;TRequest, TItem&gt;</c>.</summary>
    IStreamPipelineBehavior,

    /// <summary><c>IRequestContextFactory&lt;TContext&gt;</c>.</summary>
    IRequestContextFactory,

    /// <summary>The generator-emitted <c>CqrsHandledRequestAttribute</c>.</summary>
    CqrsHandledRequestAttribute,

    /// <summary>The generator-emitted <c>CqrsHandledNotificationAttribute</c>.</summary>
    CqrsHandledNotificationAttribute
}