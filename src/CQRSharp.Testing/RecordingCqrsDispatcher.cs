using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace CQRSharp.Testing;

/// <summary>
///     A fake <see cref="ICqrsDispatcher" /> for unit-testing code that <em>depends on</em> the dispatcher
///     (controllers, endpoints, application services) without building a container or running any pipeline. It
///     records everything dispatched through it, in order, and answers requests from stubs the test configures.
/// </summary>
/// <remarks>
///     <para>
///         Unstubbed behaviour is chosen so a forgotten stub fails loudly instead of handing the code under test a
///         <see langword="null" />: a plain <see cref="ICommand" /> succeeds with
///         <see cref="CommandResult.FromSuccess" />, a published notification completes, and any value-returning
///         request or stream throws an <see cref="InvalidOperationException" /> naming the unstubbed request type.
///     </para>
///     <para>
///         Stubs are keyed by the request's runtime type; a request whose exact type has no stub falls back to the
///         nearest base class that has one. Interfaces are not considered. Configuring the same type twice replaces
///         the earlier stub. All members are thread-safe, and the recorded views are snapshots, so they can be
///         enumerated while the code under test is still dispatching.
///     </para>
///     <para>
///         This is a test double for the dispatcher's <em>callers</em>. It runs no handlers, behaviors, or
///         validation; to test those, resolve the real dispatcher from a container built with <c>AddCqrsGenerated</c>.
///     </para>
/// </remarks>
public sealed class RecordingCqrsDispatcher : ICqrsDispatcher
{
    // Boxed delegates keyed by request type: the generic Setup methods close over the typed delegate, so no
    // reflection is needed at dispatch time (and nothing here is hostile to trimming).
    private readonly ConcurrentDictionary<Type, Func<object, CancellationToken, Task<object?>>> _responses = new();
    private readonly ConcurrentDictionary<Type, StreamStub> _streams = new();
    private readonly ConcurrentDictionary<Type, Func<object, Exception>> _failures = new();

    private readonly object _gate = new();
    private readonly List<DispatchedMessage> _log = [];

    /// <summary>Everything dispatched so far (sends, streams, and publishes interleaved), in call order.</summary>
    public IReadOnlyList<DispatchedMessage> Dispatched
    {
        get
        {
            lock (_gate)
                return _log.ToArray();
        }
    }

    /// <summary>Every request passed to a <c>Send</c> overload, in call order.</summary>
    public IReadOnlyList<object> SentRequests => Messages(DispatchKind.Send);

    /// <summary>Every request passed to a <c>Stream</c> overload, in call order (recorded when the stream is requested, not when it is enumerated).</summary>
    public IReadOnlyList<object> StartedStreams => Messages(DispatchKind.Stream);

    /// <summary>Every notification passed to <c>Publish</c>, in call order.</summary>
    public IReadOnlyList<object> PublishedNotifications => Messages(DispatchKind.Publish);

    /// <summary>The sent requests assignable to <typeparamref name="TRequest" />, in call order.</summary>
    /// <typeparam name="TRequest">The request type (or a base type/interface) to filter by.</typeparam>
    public IReadOnlyList<TRequest> Sent<TRequest>() => SentRequests.OfType<TRequest>().ToArray();

    /// <summary>The started stream requests assignable to <typeparamref name="TRequest" />, in call order.</summary>
    /// <typeparam name="TRequest">The stream request type (or a base type/interface) to filter by.</typeparam>
    public IReadOnlyList<TRequest> Streamed<TRequest>() => StartedStreams.OfType<TRequest>().ToArray();

    /// <summary>The published notifications assignable to <typeparamref name="TNotification" />, in call order.</summary>
    /// <typeparam name="TNotification">The notification type (or a base type/interface) to filter by.</typeparam>
    public IReadOnlyList<TNotification> Published<TNotification>() => PublishedNotifications.OfType<TNotification>().ToArray();

    /// <summary>Stubs the response for <typeparamref name="TRequest" />, computed from the request that was sent.</summary>
    /// <typeparam name="TRequest">The request type to answer.</typeparam>
    /// <typeparam name="TResponse">The request's response type.</typeparam>
    /// <param name="respond">Produces the response. An exception it throws surfaces as a faulted <c>Send</c> task.</param>
    /// <returns>This dispatcher, for chaining.</returns>
    public RecordingCqrsDispatcher Setup<TRequest, TResponse>(Func<TRequest, TResponse> respond)
        where TRequest : IRequest<TResponse>
    {
        ArgumentNullException.ThrowIfNull(respond);
        _responses[typeof(TRequest)] = (request, _) => Task.FromResult<object?>(respond((TRequest)request));
        return this;
    }

    /// <summary>Stubs a fixed response for every <typeparamref name="TRequest" />.</summary>
    /// <typeparam name="TRequest">The request type to answer.</typeparam>
    /// <typeparam name="TResponse">The request's response type.</typeparam>
    /// <param name="response">The response every send of <typeparamref name="TRequest" /> returns.</param>
    /// <returns>This dispatcher, for chaining.</returns>
    public RecordingCqrsDispatcher Setup<TRequest, TResponse>(TResponse response)
        where TRequest : IRequest<TResponse>
    {
        _responses[typeof(TRequest)] = (_, _) => Task.FromResult<object?>(response);
        return this;
    }

    /// <summary>Stubs an asynchronous response for <typeparamref name="TRequest" />, e.g. to hold a send open or observe its cancellation token.</summary>
    /// <typeparam name="TRequest">The request type to answer.</typeparam>
    /// <typeparam name="TResponse">The request's response type.</typeparam>
    /// <param name="respond">Produces the response from the request and the token passed to <c>Send</c>.</param>
    /// <returns>This dispatcher, for chaining.</returns>
    public RecordingCqrsDispatcher SetupAsync<TRequest, TResponse>(Func<TRequest, CancellationToken, Task<TResponse>> respond)
        where TRequest : IRequest<TResponse>
    {
        ArgumentNullException.ThrowIfNull(respond);
        _responses[typeof(TRequest)] = async (request, cancellationToken) =>
            await respond((TRequest)request, cancellationToken).ConfigureAwait(false);
        return this;
    }

    /// <summary>Stubs the items streamed for <typeparamref name="TRequest" /> from an in-memory sequence.</summary>
    /// <typeparam name="TRequest">The stream request type to answer.</typeparam>
    /// <typeparam name="TItem">The stream's element type.</typeparam>
    /// <param name="items">Produces the items. It runs when the stream is enumerated; cancellation is honoured between items.</param>
    /// <returns>This dispatcher, for chaining.</returns>
    public RecordingCqrsDispatcher SetupStream<TRequest, TItem>(Func<TRequest, IEnumerable<TItem>> items)
        where TRequest : IStreamRequest<TItem>
    {
        ArgumentNullException.ThrowIfNull(items);
        return SetupStream<TRequest, TItem>((request, cancellationToken) => Enumerate(items, request, cancellationToken));
    }

    /// <summary>Stubs the stream for <typeparamref name="TRequest" /> with a real asynchronous sequence.</summary>
    /// <typeparam name="TRequest">The stream request type to answer.</typeparam>
    /// <typeparam name="TItem">The stream's element type.</typeparam>
    /// <param name="stream">Produces the stream from the request and the token passed to <c>Stream</c>.</param>
    /// <returns>This dispatcher, for chaining.</returns>
    public RecordingCqrsDispatcher SetupStream<TRequest, TItem>(Func<TRequest, CancellationToken, IAsyncEnumerable<TItem>> stream)
        where TRequest : IStreamRequest<TItem>
    {
        ArgumentNullException.ThrowIfNull(stream);
        // Both shapes are captured here, where TItem is known, so the untyped Stream(object) overload can box items
        // without reflecting over the request's interfaces.
        // Deferred: the delegate runs on the first MoveNextAsync, so a stub that throws surfaces there, as a real
        // stream's failure does, not at the Stream(...) call.
        _streams[typeof(TRequest)] = new StreamStub(
            (request, cancellationToken) => Defer(() => stream((TRequest)request, cancellationToken), cancellationToken),
            (request, cancellationToken) => Box(Defer(() => stream((TRequest)request, cancellationToken), cancellationToken), cancellationToken));
        return this;
    }

    /// <summary>
    ///     Makes dispatching a <typeparamref name="TMessage" /> fail. A send returns a faulted task, a stream throws when
    ///     enumerated, and a publish returns a faulted task, mirroring where a real handler failure would surface.
    ///     Takes precedence over any response or stream stub for the same message.
    /// </summary>
    /// <typeparam name="TMessage">The request or notification type that should fail.</typeparam>
    /// <param name="exception">The exception to surface.</param>
    /// <returns>This dispatcher, for chaining.</returns>
    public RecordingCqrsDispatcher Throws<TMessage>(Exception exception)
        where TMessage : notnull
    {
        ArgumentNullException.ThrowIfNull(exception);
        _failures[typeof(TMessage)] = _ => exception;
        return this;
    }

    /// <summary>Makes dispatching a <typeparamref name="TMessage" /> fail with an exception built from the message. See <see cref="Throws{TMessage}(Exception)" />.</summary>
    /// <typeparam name="TMessage">The request or notification type that should fail.</typeparam>
    /// <param name="exceptionFactory">Builds the exception to surface from the dispatched message.</param>
    /// <returns>This dispatcher, for chaining.</returns>
    public RecordingCqrsDispatcher Throws<TMessage>(Func<TMessage, Exception> exceptionFactory)
        where TMessage : notnull
    {
        ArgumentNullException.ThrowIfNull(exceptionFactory);
        _failures[typeof(TMessage)] = message => exceptionFactory((TMessage)message);
        return this;
    }

    /// <summary>Forgets everything recorded so far; stubs are kept. Useful between the arrange and act phases of a test.</summary>
    public void ClearRecorded()
    {
        lock (_gate)
            _log.Clear();
    }

    /// <inheritdoc />
    public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        RejectStreamRequest(request);
        Record(DispatchKind.Send, request);

        return SendCore<TResponse>(request, Resolve(request), cancellationToken);
    }

    /// <inheritdoc />
    public Task<object?> Send(object request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Same argument contract as the real dispatcher, so a misuse is caught in the unit test too.
        if (request is not IRequest)
            throw new ArgumentException($"Request must implement {nameof(IRequest)}.", nameof(request));

        RejectStreamRequest(request);
        Record(DispatchKind.Send, request);

        return SendCore<object?>(request, Resolve(request), cancellationToken);
    }

    /// <inheritdoc />
    public IAsyncEnumerable<TItem> Stream<TItem>(IStreamRequest<TItem> request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        Record(DispatchKind.Stream, request);

        if (Find(_failures, request.GetType()) is { } failure)
            return Fail<TItem>(failure(request));

        var stub = Find(_streams, request.GetType()) ?? throw Unstubbed(request, "stream", nameof(SetupStream));

        return stub.Typed(request, cancellationToken) as IAsyncEnumerable<TItem>
               ?? throw new InvalidOperationException(
                   $"The stream stubbed for {request.GetType().FullName} does not produce {typeof(TItem).FullName} items.");
    }

    /// <inheritdoc />
    public IAsyncEnumerable<object?> Stream(object request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request is not IStreamRequest)
            throw new ArgumentException($"Request must implement {nameof(IStreamRequest)}.", nameof(request));

        Record(DispatchKind.Stream, request);

        if (Find(_failures, request.GetType()) is { } failure)
            return Fail<object?>(failure(request));

        var stub = Find(_streams, request.GetType()) ?? throw Unstubbed(request, "stream", nameof(SetupStream));
        return stub.Boxed(request, cancellationToken);
    }

    /// <inheritdoc />
    public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
        where TNotification : INotification
    {
        ArgumentNullException.ThrowIfNull(notification);
        Record(DispatchKind.Publish, notification);

        return Find(_failures, notification.GetType()) is { } failure
            ? Task.FromException(failure(notification))
            : Task.CompletedTask;
    }

    // Resolution happens synchronously in Send so a missing stub (a test-setup mistake) throws at the call site even if
    // the code under test never awaits the task; failures the TEST configured surface through the task instead.
    private Func<object, CancellationToken, Task<object?>> Resolve(object request)
    {
        var type = request.GetType();

        if (Find(_failures, type) is { } failure)
            return (message, _) => Task.FromException<object?>(failure(message));

        if (Find(_responses, type) is { } respond)
            return respond;

        // Only a plain command has an obviously-right default. Value-returning requests have none: inventing
        // default(TResponse) would push a null into the code under test and fail far from the cause.
        if (request is ICommand)
            return static (_, _) => Task.FromResult<object?>(CommandResult.FromSuccess());

        throw Unstubbed(request, "response", nameof(Setup));
    }

    private static async Task<TResponse> SendCore<TResponse>(
        object request,
        Func<object, CancellationToken, Task<object?>> respond,
        CancellationToken cancellationToken)
    {
        // Awaiting inside an async method turns a stub that throws synchronously into a faulted task, which is how a
        // real handler failure reaches the caller.
        var response = await respond(request, cancellationToken).ConfigureAwait(false);

        return response switch
        {
            TResponse typed => typed,
            null when default(TResponse) is null => default!,
            _ => throw new InvalidOperationException(
                $"The response stubbed for {request.GetType().FullName} is {response?.GetType().FullName ?? "null"}, " +
                $"which is not the {typeof(TResponse).FullName} the caller asked for.")
        };
    }

    private static void RejectStreamRequest(object request)
    {
        if (request is IStreamRequest)
            throw new InvalidOperationException("Stream requests must be executed via Stream(...) instead of Send(...).");
    }

    private static InvalidOperationException Unstubbed(object request, string what, string setupMethod)
        => new($"No {what} is stubbed for {request.GetType().FullName}. " +
               $"Call {nameof(RecordingCqrsDispatcher)}.{setupMethod}<{request.GetType().Name}, ...>(...) before the code under test dispatches it.");

    // Exact type first, then the nearest base class: lets one stub on a base request cover its subclasses without
    // enumerating interfaces (which would need reflection the trimmer cannot see through).
    private static TStub? Find<TStub>(ConcurrentDictionary<Type, TStub> stubs, Type messageType)
        where TStub : class
    {
        for (var type = messageType; type is not null; type = type.BaseType)
        {
            if (stubs.TryGetValue(type, out var stub))
                return stub;
        }

        return null;
    }

    private void Record(DispatchKind kind, object message)
    {
        lock (_gate)
            _log.Add(new DispatchedMessage(kind, message));
    }

    private object[] Messages(DispatchKind kind)
    {
        lock (_gate)
            return _log.Where(entry => entry.Kind == kind).Select(entry => entry.Message).ToArray();
    }

    private static async IAsyncEnumerable<TItem> Enumerate<TRequest, TItem>(
        Func<TRequest, IEnumerable<TItem>> items,
        TRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // An async iterator must await something; the sequence itself is synchronous by design.
        await Task.CompletedTask.ConfigureAwait(false);

        foreach (var item in items(request))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return item;
        }
    }

    private static async IAsyncEnumerable<TItem> Defer<TItem>(
        Func<IAsyncEnumerable<TItem>> create,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var item in create().WithCancellation(cancellationToken).ConfigureAwait(false))
            yield return item;
    }

    private static async IAsyncEnumerable<object?> Box<TItem>(
        IAsyncEnumerable<TItem> source,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var item in source.WithCancellation(cancellationToken).ConfigureAwait(false))
            yield return item;
    }

    private static async IAsyncEnumerable<TItem> Fail<TItem>(Exception exception)
    {
        // Awaiting a faulted task is what throws: the failure stays lazy (it surfaces on the first MoveNextAsync, like
        // a failing stream handler) without an unreachable-yield iterator.
        await Task.FromException(exception).ConfigureAwait(false);
        yield break;
    }

    private sealed record StreamStub(
        Func<object, CancellationToken, object> Typed,
        Func<object, CancellationToken, IAsyncEnumerable<object?>> Boxed);
}
