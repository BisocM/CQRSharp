using CQRSharp.Abstractions.Interfaces.Markers.Request;
using CQRSharp.Pipelines.Behaviors.RateLimiting.Context;

namespace CQRSharp.Tests.Shared.CollisionsB;

public sealed class CollisionCommand : RequestBase<IRateLimitedContext>;
