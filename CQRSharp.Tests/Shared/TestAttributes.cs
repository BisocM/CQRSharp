using CQRSharp.Abstractions.Data.Attributes.Pipelines;
using CQRSharp.Abstractions.Data.Interfaces.Markers.Request;

namespace CQRSharp.Tests.Shared;

/// <summary>
/// A test implementation of a pre-handler attribute with a configurable execution priority.
/// </summary>
/// <param name="priority">The execution priority of the pre-handler.</param>
public class TestPreHandlerAttribute(int priority) : Attribute, IPreHandlerAttribute
{
    /// <inheritdoc />
    public int PreHandlerExecutionPriority { get; } = priority;

    /// <inheritdoc />
    public virtual Task OnBeforeHandle(IRequest request, IServiceProvider serviceProvider, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// A test implementation of a post-handler attribute with a configurable execution priority.
/// </summary>
/// <param name="priority">The execution priority of the post-handler.</param>
public class TestPostHandlerAttribute(int priority) : Attribute, IPostHandlerAttribute
{
    /// <inheritdoc />
    public int PostHandlerExecutionPriority { get; } = priority;

    /// <inheritdoc />
    public virtual Task OnAfterHandle(IRequest request, IServiceProvider serviceProvider, CancellationToken cancellationToken) => Task.CompletedTask;
}