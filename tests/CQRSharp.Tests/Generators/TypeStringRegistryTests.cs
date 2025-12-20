using System.Reflection;
using CQRSharp.Abstractions.SourceGeneration;

namespace CQRSharp.Tests.Generators;

/// <summary>
///     Contains tests to ensure that the type strings defined in <see cref="TypeStrings" />
///     resolve to actual, valid types within their specified assemblies. This is crucial
///     for the correctness of the source generators that rely on them.
/// </summary>
public class TypeStringRegistryTests
{
    [Theory]
    // CQRSharp.Abstractions
    [InlineData(TypeStrings.ICommand, "CQRSharp.Abstractions")]
    [InlineData(TypeStrings.IQuery, "CQRSharp.Abstractions")]
    [InlineData(TypeStrings.RequestBaseGeneric, "CQRSharp.Abstractions")]
    [InlineData(TypeStrings.ICommandHandler1, "CQRSharp.Abstractions")]
    [InlineData(TypeStrings.ICommandHandler2, "CQRSharp.Abstractions")]
    [InlineData(TypeStrings.IQueryHandler2, "CQRSharp.Abstractions")]
    [InlineData(TypeStrings.IQueryHandler3, "CQRSharp.Abstractions")]
    [InlineData(TypeStrings.INotification, "CQRSharp.Abstractions")]
    [InlineData(TypeStrings.INotificationHandler, "CQRSharp.Abstractions")]
    [InlineData(TypeStrings.IRequestValidator1, "CQRSharp.Abstractions")]
    [InlineData(TypeStrings.IPreHandlerAttribute, "CQRSharp.Abstractions")]
    [InlineData(TypeStrings.IPostHandlerAttribute, "CQRSharp.Abstractions")]
    [InlineData(TypeStrings.PipelineExemptionAttribute, "CQRSharp.Abstractions")]
    [InlineData(TypeStrings.PipelinePriorityAttribute, "CQRSharp.Abstractions")]
    [InlineData(TypeStrings.NotificationNameAttribute, "CQRSharp.Abstractions")]
    [InlineData(TypeStrings.RequestContextBase, "CQRSharp.Abstractions")]
    [InlineData(TypeStrings.CommandResult, "CQRSharp.Abstractions")]
    [InlineData(TypeStrings.PropertySensitivity, "CQRSharp.Abstractions")]
    // CQRSharp.Core
    [InlineData(TypeStrings.IPipelineBehavior, "CQRSharp.Core")]
    [InlineData(TypeStrings.IRequestContextFactory, "CQRSharp.Core")]
    public void TypeString_ShouldResolveToValidType(string metadataName, string assemblyName)
    {
        // Arrange
        var assembly = Assembly.Load(assemblyName);

        // Act
        var resolvedType = assembly.GetType(metadataName);

        // Assert
        Assert.NotNull(resolvedType); // This ensures the type name and namespace are correct and the type exists.
    }
}
