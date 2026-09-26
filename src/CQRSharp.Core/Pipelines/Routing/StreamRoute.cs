using System.ComponentModel;
using System.Runtime.CompilerServices;
using CQRSharp.Core.Diagnostics;

namespace CQRSharp.Core.Pipelines;

/// <summary>
///     How one streaming request type is dispatched and described: the executor's streaming entry point closed over the
///     request and item types. A source-generated module creates one per stream request its assembly handles, through
///     <see cref="For{TRequest, TItem}" />.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public abstract class StreamRoute
{
    private protected StreamRoute()
    {
    }

    /// <summary>The route of a stream request producing <typeparamref name="TItem" />s.</summary>
    /// <typeparam name="TRequest">The stream request type.</typeparam>
    /// <typeparam name="TItem">The streamed item type.</typeparam>
    public static StreamRoute For<TRequest, TItem>() where TRequest : IStreamRequest<TItem> => Route<TRequest, TItem>.Instance;

    /// <summary>Dispatches the request; the result is an <c>IAsyncEnumerable&lt;TItem&gt;</c>.</summary>
    internal abstract object Execute(PipelineExecutor executor, IStreamRequest request, CancellationToken cancellationToken);

    /// <summary>Dispatches the request and boxes its items, for the untyped <c>Stream(object)</c> path.</summary>
    internal abstract IAsyncEnumerable<object?> ExecuteBoxed(PipelineExecutor executor, IStreamRequest request, CancellationToken cancellationToken);

    /// <summary>Describes how the request is wired in the describer's scope.</summary>
    internal abstract CqrsRequestBinding Describe(RequestBindingDescriber describer);

    private sealed class Route<TRequest, TItem> : StreamRoute where TRequest : IStreamRequest<TItem>
    {
        public static readonly Route<TRequest, TItem> Instance = new();

        internal override object Execute(PipelineExecutor executor, IStreamRequest request, CancellationToken cancellationToken)
            => executor.ExecuteStreamAsync<TRequest, TItem>((TRequest)request, cancellationToken);

        internal override IAsyncEnumerable<object?> ExecuteBoxed(PipelineExecutor executor, IStreamRequest request, CancellationToken cancellationToken)
            => Box(executor.ExecuteStreamAsync<TRequest, TItem>((TRequest)request, cancellationToken), cancellationToken);

        internal override CqrsRequestBinding Describe(RequestBindingDescriber describer)
            => describer.DescribeStream<TRequest, TItem>();

        private static async IAsyncEnumerable<object?> Box(
            IAsyncEnumerable<TItem> source,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var item in source.WithCancellation(cancellationToken).ConfigureAwait(false))
                yield return item;
        }
    }
}
