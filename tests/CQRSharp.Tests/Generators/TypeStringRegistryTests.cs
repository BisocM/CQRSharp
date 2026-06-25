using CQRSharp.Abstractions.Attributes.Notifications;
using CQRSharp.Abstractions.Attributes.Pipelines;
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
using CQRSharp.Core.Pipelines;

namespace CQRSharp.Tests.Generators;

/// <summary>
///     Drift guard: the source generator resolves framework types by their fully-qualified metadata names
///     (in the generator-internal <c>TypeStrings</c>/<c>CoreTypeStrings</c>). The expected names are inlined here and
///     asserted against the real type <see cref="Type.FullName" />, so renaming or moving a framework type that the
///     generator depends on fails this test.
/// </summary>
public sealed class TypeStringRegistryTests
{
    public static IEnumerable<object[]> Cases =>
    [
        // CQRSharp.Abstractions
        ["CQRSharp.Abstractions.Interfaces.Markers.Command.ICommand", "CQRSharp.Abstractions", typeof(ICommand)],
        ["CQRSharp.Abstractions.Interfaces.Markers.Query.IQuery`1", "CQRSharp.Abstractions", typeof(IQuery<>)],
        ["CQRSharp.Abstractions.Interfaces.Markers.Stream.IStreamRequest`1", "CQRSharp.Abstractions", typeof(IStreamRequest<>)],
        ["CQRSharp.Abstractions.Interfaces.Markers.Request.RequestBase`1", "CQRSharp.Abstractions", typeof(RequestBase<>)],
        ["CQRSharp.Abstractions.Interfaces.Handlers.ICommandHandler`1", "CQRSharp.Abstractions", typeof(ICommandHandler<>)],
        ["CQRSharp.Abstractions.Interfaces.Handlers.ICommandHandler`2", "CQRSharp.Abstractions", typeof(ICommandHandler<,>)],
        ["CQRSharp.Abstractions.Interfaces.Handlers.IQueryHandler`2", "CQRSharp.Abstractions", typeof(IQueryHandler<,>)],
        ["CQRSharp.Abstractions.Interfaces.Handlers.IQueryHandler`3", "CQRSharp.Abstractions", typeof(IQueryHandler<,,>)],
        ["CQRSharp.Abstractions.Interfaces.Handlers.IStreamRequestHandler`2", "CQRSharp.Abstractions", typeof(IStreamRequestHandler<,>)],
        ["CQRSharp.Abstractions.Interfaces.Handlers.IStreamRequestHandler`3", "CQRSharp.Abstractions", typeof(IStreamRequestHandler<,,>)],
        ["CQRSharp.Abstractions.Interfaces.Notifications.INotification", "CQRSharp.Abstractions", typeof(INotification)],
        ["CQRSharp.Abstractions.Interfaces.Notifications.INotificationHandler`1", "CQRSharp.Abstractions", typeof(INotificationHandler<>)],
        ["CQRSharp.Abstractions.Interfaces.Validation.IRequestValidator`1", "CQRSharp.Abstractions", typeof(IRequestValidator<>)],
        ["CQRSharp.Abstractions.Interfaces.Exceptions.IRequestExceptionHandler`3", "CQRSharp.Abstractions", typeof(IRequestExceptionHandler<,,>)],
        ["CQRSharp.Abstractions.Interfaces.Exceptions.IRequestExceptionAction`2", "CQRSharp.Abstractions", typeof(IRequestExceptionAction<,>)],
        ["CQRSharp.Abstractions.Attributes.Pipelines.IPreHandlerAttribute", "CQRSharp.Abstractions", typeof(IPreHandlerAttribute)],
        ["CQRSharp.Abstractions.Attributes.Pipelines.IPostHandlerAttribute", "CQRSharp.Abstractions", typeof(IPostHandlerAttribute)],
        ["CQRSharp.Abstractions.Attributes.Pipelines.PipelineExemptionAttribute", "CQRSharp.Abstractions", typeof(PipelineExemptionAttribute)],
        ["CQRSharp.Abstractions.Attributes.Notifications.NotificationNameAttribute", "CQRSharp.Abstractions", typeof(NotificationNameAttribute)],
        ["CQRSharp.Abstractions.Interfaces.Context.RequestContextBase", "CQRSharp.Abstractions", typeof(RequestContextBase)],
        ["CQRSharp.Abstractions.Models.Commands.CommandResult", "CQRSharp.Abstractions", typeof(CommandResult)],
        // CQRSharp.Core
        ["CQRSharp.Core.Pipelines.IPipelineBehavior`2", "CQRSharp.Core", typeof(IPipelineBehavior<,>)],
        ["CQRSharp.Core.Pipelines.IStreamPipelineBehavior`2", "CQRSharp.Core", typeof(IStreamPipelineBehavior<,>)],
        ["CQRSharp.Core.Factories.IRequestContextFactory`1", "CQRSharp.Core", typeof(IRequestContextFactory<>)],
    ];

    [Theory]
    [MemberData(nameof(Cases))]
    public void TypeString_ShouldResolveToValidType(string metadataName, string assemblyName, Type runtimeType)
    {
        Assert.Equal(metadataName, runtimeType.FullName);
        Assert.Equal(assemblyName, runtimeType.Assembly.GetName().Name);
    }
}
