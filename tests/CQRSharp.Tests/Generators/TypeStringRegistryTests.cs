using CQRSharp.Abstractions.Data.Attributes.Notifications;
using CQRSharp.Abstractions.Data.Attributes.Pipelines;
using CQRSharp.Abstractions.Data.Interfaces.Context;
using CQRSharp.Abstractions.Data.Interfaces.Exceptions;
using CQRSharp.Abstractions.Data.Interfaces.Handlers;
using CQRSharp.Abstractions.Data.Interfaces.Markers.Command;
using CQRSharp.Abstractions.Data.Interfaces.Markers.Query;
using CQRSharp.Abstractions.Data.Interfaces.Markers.Request;
using CQRSharp.Abstractions.Data.Interfaces.Markers.Stream;
using CQRSharp.Abstractions.Data.Interfaces.Notifications;
using CQRSharp.Abstractions.Data.Interfaces.Validation;
using CQRSharp.Abstractions.Data.Models.Commands;
using CQRSharp.Abstractions.Data.Models.Requests;
using CQRSharp.Abstractions.SourceGeneration;
using CQRSharp.Core.Factories;
using CQRSharp.Core.Pipelines;

namespace CQRSharp.Tests.Generators;

public sealed class TypeStringRegistryTests
{
    public static IEnumerable<object[]> Cases =>
    [
        // CQRSharp.Abstractions
        [TypeStrings.ICommand, "CQRSharp.Abstractions", typeof(ICommand)],
        [TypeStrings.IQuery, "CQRSharp.Abstractions", typeof(IQuery<>)],
        [TypeStrings.IStreamRequest, "CQRSharp.Abstractions", typeof(IStreamRequest<>)],
        [TypeStrings.RequestBaseGeneric, "CQRSharp.Abstractions", typeof(RequestBase<>)],
        [TypeStrings.ICommandHandler1, "CQRSharp.Abstractions", typeof(ICommandHandler<>)],
        [TypeStrings.ICommandHandler2, "CQRSharp.Abstractions", typeof(ICommandHandler<,>)],
        [TypeStrings.IQueryHandler2, "CQRSharp.Abstractions", typeof(IQueryHandler<,>)],
        [TypeStrings.IQueryHandler3, "CQRSharp.Abstractions", typeof(IQueryHandler<,,>)],
        [TypeStrings.IStreamRequestHandler2, "CQRSharp.Abstractions", typeof(IStreamRequestHandler<,>)],
        [TypeStrings.IStreamRequestHandler3, "CQRSharp.Abstractions", typeof(IStreamRequestHandler<,,>)],
        [TypeStrings.INotification, "CQRSharp.Abstractions", typeof(INotification)],
        [TypeStrings.INotificationHandler, "CQRSharp.Abstractions", typeof(INotificationHandler<>)],
        [TypeStrings.IRequestValidator1, "CQRSharp.Abstractions", typeof(IRequestValidator<>)],
        [TypeStrings.IRequestExceptionHandler3, "CQRSharp.Abstractions", typeof(IRequestExceptionHandler<,,>)],
        [TypeStrings.IRequestExceptionAction2, "CQRSharp.Abstractions", typeof(IRequestExceptionAction<,>)],
        [TypeStrings.IPreHandlerAttribute, "CQRSharp.Abstractions", typeof(IPreHandlerAttribute)],
        [TypeStrings.IPostHandlerAttribute, "CQRSharp.Abstractions", typeof(IPostHandlerAttribute)],
        [TypeStrings.PipelineExemptionAttribute, "CQRSharp.Abstractions", typeof(PipelineExemptionAttribute)],
        [TypeStrings.NotificationNameAttribute, "CQRSharp.Abstractions", typeof(NotificationNameAttribute)],
        [TypeStrings.RequestContextBase, "CQRSharp.Abstractions", typeof(RequestContextBase)],
        [TypeStrings.CommandResult, "CQRSharp.Abstractions", typeof(CommandResult)],
        // CQRSharp.Core
        [TypeStrings.IPipelineBehavior, "CQRSharp.Core", typeof(IPipelineBehavior<,>)],
        [TypeStrings.IStreamPipelineBehavior, "CQRSharp.Core", typeof(IStreamPipelineBehavior<,>)],
        [TypeStrings.IRequestContextFactory, "CQRSharp.Core", typeof(IRequestContextFactory<>)],
    ];

    [Theory]
    [MemberData(nameof(Cases))]
    public void TypeString_ShouldResolveToValidType(string metadataName, string assemblyName, Type runtimeType)
    {
        Assert.Equal(metadataName, runtimeType.FullName);
        Assert.Equal(assemblyName, runtimeType.Assembly.GetName().Name);
    }
}
