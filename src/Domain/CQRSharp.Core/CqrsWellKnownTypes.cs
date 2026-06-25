using CQRSharp.Abstractions.Attributes.Notifications;
using CQRSharp.Abstractions.Attributes.Pipelines;
using CQRSharp.Abstractions.Attributes.SourceGeneration;
using CQRSharp.Abstractions.Interfaces.Context;
using CQRSharp.Abstractions.Interfaces.Exceptions;
using CQRSharp.Abstractions.Interfaces.Handlers;
using CQRSharp.Abstractions.Interfaces.Markers.Command;
using CQRSharp.Abstractions.Interfaces.Markers.Query;
using CQRSharp.Abstractions.Interfaces.Markers.Request;
using CQRSharp.Abstractions.Interfaces.Markers.Stream;
using CQRSharp.Abstractions.Interfaces.Notifications;
using CQRSharp.Abstractions.Interfaces.Validation;
using CQRSharp.Abstractions.Models.Commands;
using CQRSharp.Core.Factories;
using CQRSharp.Core.Mediation;
using CQRSharp.Core.Pipelines;

// CQRSharp's single source of truth for framework "well-known" types. One entry per CqrsRole, each a real typeof(...).
// Renaming or moving any anchor type is a COMPILE ERROR here — the source generator and analyzers read these back from
// Core's referenced metadata (see CqrsKnownSymbols) rather than matching name strings, so drift can no longer silently
// disable code generation. Unbound open generics (e.g. typeof(ICommandHandler<,>)) encode arity structurally, so an
// arity change is likewise a compile error. Core is the only assembly with typeof() line-of-sight to ICqrsDispatcher
// (its own) and every CQRSharp.Abstractions anchor (referenced one-directionally).

[assembly: CqrsWellKnownType(CqrsRole.Dispatcher, typeof(ICqrsDispatcher))]
[assembly: CqrsWellKnownType(CqrsRole.IRequest, typeof(IRequest))]
[assembly: CqrsWellKnownType(CqrsRole.INotification, typeof(INotification))]
[assembly: CqrsWellKnownType(CqrsRole.ICommand, typeof(ICommand))]
[assembly: CqrsWellKnownType(CqrsRole.IQuery, typeof(IQuery<>))]
[assembly: CqrsWellKnownType(CqrsRole.IStreamRequest, typeof(IStreamRequest<>))]
[assembly: CqrsWellKnownType(CqrsRole.IStreamRequestMarker, typeof(IStreamRequest))]
[assembly: CqrsWellKnownType(CqrsRole.RequestBaseGeneric, typeof(RequestBase<>))]
[assembly: CqrsWellKnownType(CqrsRole.ICommandHandler1, typeof(ICommandHandler<>))]
[assembly: CqrsWellKnownType(CqrsRole.ICommandHandler2, typeof(ICommandHandler<,>))]
[assembly: CqrsWellKnownType(CqrsRole.IQueryHandler2, typeof(IQueryHandler<,>))]
[assembly: CqrsWellKnownType(CqrsRole.IQueryHandler3, typeof(IQueryHandler<,,>))]
[assembly: CqrsWellKnownType(CqrsRole.IStreamRequestHandler2, typeof(IStreamRequestHandler<,>))]
[assembly: CqrsWellKnownType(CqrsRole.IStreamRequestHandler3, typeof(IStreamRequestHandler<,,>))]
[assembly: CqrsWellKnownType(CqrsRole.INotificationHandler, typeof(INotificationHandler<>))]
[assembly: CqrsWellKnownType(CqrsRole.IRequestValidator1, typeof(IRequestValidator<>))]
[assembly: CqrsWellKnownType(CqrsRole.IRequestExceptionHandler3, typeof(IRequestExceptionHandler<,,>))]
[assembly: CqrsWellKnownType(CqrsRole.IRequestExceptionAction2, typeof(IRequestExceptionAction<,>))]
[assembly: CqrsWellKnownType(CqrsRole.IPreHandlerAttribute, typeof(IPreHandlerAttribute))]
[assembly: CqrsWellKnownType(CqrsRole.IPostHandlerAttribute, typeof(IPostHandlerAttribute))]
[assembly: CqrsWellKnownType(CqrsRole.PipelineExemptionAttribute, typeof(PipelineExemptionAttribute))]
[assembly: CqrsWellKnownType(CqrsRole.NotificationNameAttribute, typeof(NotificationNameAttribute))]
[assembly: CqrsWellKnownType(CqrsRole.RequestContextBase, typeof(RequestContextBase))]
[assembly: CqrsWellKnownType(CqrsRole.CommandResult, typeof(CommandResult))]
[assembly: CqrsWellKnownType(CqrsRole.IPipelineBehavior, typeof(IPipelineBehavior<,>))]
[assembly: CqrsWellKnownType(CqrsRole.IStreamPipelineBehavior, typeof(IStreamPipelineBehavior<,>))]
[assembly: CqrsWellKnownType(CqrsRole.IRequestContextFactory, typeof(IRequestContextFactory<>))]
[assembly: CqrsWellKnownType(CqrsRole.CqrsHandledRequestAttribute, typeof(CqrsHandledRequestAttribute))]
[assembly: CqrsWellKnownType(CqrsRole.CqrsHandledNotificationAttribute, typeof(CqrsHandledNotificationAttribute))]
