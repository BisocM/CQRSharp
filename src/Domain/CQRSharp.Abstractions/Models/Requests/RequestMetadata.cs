using CQRSharp.Abstractions.Attributes.Pipelines;

namespace CQRSharp.Abstractions.Models.Requests;

/// <summary>
///     Represents metadata for a specific request, including its associations with
///     handlers, pipeline behaviors, and the request's context type.
///     This metadata is used to provide configuration and control over request processing mechanisms.
/// </summary>
/// <param name="RequestType">
///     The type of the request associated with this metadata.
/// </param>
/// <param name="HandlerType">
///     The type of the handler responsible for processing the request, if any.
/// </param>
/// <param name="PreHandlers">
///     An array of pre-handler attributes that define behaviors to execute prior to the main handler.
/// </param>
/// <param name="PostHandlers">
///     An array of post-handler attributes that define behaviors to execute after the main handler.
/// </param>
/// <param name="PipelineExemptions">
///     An optional attribute specifying exemptions for the request from certain pipeline behaviors.
/// </param>
/// <param name="ResultType">
///     The type of the result expected from the request, if applicable. NULL for commands.
/// </param>
/// <param name="ContextType">
///     The type of the context that should be used to process this request. This typically is the type parameter TContext
///     in RequestBase&lt;TContext&gt; (or RequestContextBase as a fallback).
/// </param>
public sealed record RequestMetadata(
    Type RequestType,
    Type? HandlerType,
    IPreHandlerAttribute[] PreHandlers,
    IPostHandlerAttribute[] PostHandlers,
    PipelineExemptionAttribute[] PipelineExemptions,
    Type? ResultType,
    Type? ContextType
);
