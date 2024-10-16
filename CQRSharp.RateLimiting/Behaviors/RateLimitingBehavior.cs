using CQRSharp.Interfaces.Markers.Request;
using Microsoft.Extensions.Logging;
using CQRSharp.Core.Pipeline;
using CQRSharp.Core.Pipeline.Attributes;
using CQRSharp.RateLimiting.Handlers;
using CQRSharp.RateLimiting.Exceptions;

namespace CQRSharp.RateLimiting.Behaviors
{
    [PipelinePriority(0)]
    public sealed class RateLimitingBehavior<TRequest, TResult>(
        ILogger<RateLimitingBehavior<TRequest, TResult>> logger,
        RateLimiter rateLimiter,
        IUserIdentifierFactory userIdentifierFactory) : IPipelineBehavior<TRequest, TResult> where TRequest : RequestBase
    {
        public async Task<TResult> Handle(TRequest request, CancellationToken cancellationToken, Func<CancellationToken, Task<TResult>> next)
        {
            //Ensure the request can be cast to RequestBase for identifier extraction
            if (request is not RequestBase baseRequest)
            {
                logger.LogError("Rate limiting failed: request must inherit from RequestBase to support rate limiting.");
                throw new InvalidOperationException("Request must inherit from RequestBase to support rate limiting.");
            }

            //Get the user identifier from the factory
            string? identifier = userIdentifierFactory.GetIdentifier(baseRequest);
            baseRequest.UserIdentifier = identifier;

            if (identifier == null)
            {
                logger.LogWarning("User identifier could not be determined for request {RequestId}.", baseRequest.Id);
                throw new InvalidOperationException("User identifier could not be determined for rate limiting.");
            }

            logger.LogInformation("Retrieved user identifier {Identifier} for request {RequestId}.", identifier, baseRequest.Id);

            //Apply rate limiting based on the user identifier
            bool isAllowed = rateLimiter.AllowRequest(identifier, baseRequest.GetType().Name);
            if (!isAllowed)
            {
                logger.LogWarning("Rate limit exceeded for user {Identifier} on request {RequestId} of type {RequestType}.",
                    identifier, baseRequest.Id, baseRequest.GetType().Name);
                throw new RateLimitExceededException(request, "Rate limit exceeded for user.");
            }

            logger.LogInformation("Request {RequestId} for user {Identifier} passed rate limiting check.", baseRequest.Id, identifier);

            return await next(cancellationToken);
        }
    }
}