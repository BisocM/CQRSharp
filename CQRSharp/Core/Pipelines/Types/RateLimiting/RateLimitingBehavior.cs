using CQRSharp.Core.Factories;
using CQRSharp.Core.Pipelines.Attributes;
using CQRSharp.Interfaces.Markers.Request;
using Microsoft.Extensions.Logging;

namespace CQRSharp.Core.Pipelines.Types.RateLimiting;

[PipelinePriority(int.MinValue)]
public sealed class RateLimitingBehavior<TRequest, TResult>(
    ILogger<RateLimitingBehavior<TRequest, TResult>> logger,
    RateLimiter rateLimiter,
    IUserIdentificationFactory userIdentifierFactory)
    : IPipelineBehavior<TRequest, TResult> where TRequest : RequestBase
{
    public async Task<TResult> Handle(TRequest request, CancellationToken cancellationToken,
        Func<CancellationToken, Task<TResult>> next)
    {
        //Ensure the request can be cast to RequestBase for identifier extraction
        if (request is not RequestBase baseRequest)
        {
            logger.LogError("Rate limiting failed: request must inherit from RequestBase to support rate limiting.");
            throw new InvalidOperationException("Request must inherit from RequestBase to support rate limiting.");
        }

        if (request.Context.UserId == null)
        {
            logger.LogWarning("User identifier could not be determined for request {RequestId}.",
                baseRequest.Context.RequestId);
            throw new InvalidOperationException("User identifier could not be determined for rate limiting.");
        }

        logger.LogInformation("Retrieved user identifier {Identifier} for request {RequestId}.", request.Context.UserId,
            baseRequest.Context.RequestId);

        //Apply rate limiting based on the user identifier
        var isAllowed = rateLimiter.AllowRequest(request.Context.UserId, baseRequest.GetType().Name);
        if (!isAllowed)
        {
            logger.LogWarning("Rate limit exceeded for user {Identifier} on request {RequestId} of type {RequestType}.",
                request.Context.UserId, baseRequest.Context.RequestId, baseRequest.GetType().Name);
            throw new RateLimitExceededException(request, "Rate limit exceeded for user.");
        }

        logger.LogInformation("Request {RequestId} for user {Identifier} passed rate limiting check.",
            baseRequest.Context.RequestId, request.Context.UserId);

        return await next(cancellationToken);
    }
}