using CQRSharp.Abstractions.Interfaces.Markers.Request;

namespace CQRSharp.Core.Pipelines;

/// <summary>
///     A source-generated route for one request type: closes the executor's generic entry point over the concrete
///     request and result types and returns the resulting <c>Task&lt;TResult&gt;</c> (as <see cref="Task" />, for the
///     dispatcher to cast back). Routes are static, stateless and AOT-safe, and are looked up by the request's exact
///     runtime type — one hash lookup however many request types an application declares.
/// </summary>
/// <param name="executor">The current scope's pipeline executor.</param>
/// <param name="request">The request; always of the type the route is registered under.</param>
/// <param name="cancellationToken">The dispatch's cancellation token.</param>
public delegate Task RequestRoute(IPipelineExecutor executor, IRequest request, CancellationToken cancellationToken);

/// <summary>The <see cref="RequestRoute" /> counterpart for the untyped <c>Send(object)</c> path: the result is boxed.</summary>
/// <param name="executor">The current scope's pipeline executor.</param>
/// <param name="request">The request; always of the type the route is registered under.</param>
/// <param name="cancellationToken">The dispatch's cancellation token.</param>
public delegate Task<object?> UntypedRequestRoute(IPipelineExecutor executor, IRequest request, CancellationToken cancellationToken);
