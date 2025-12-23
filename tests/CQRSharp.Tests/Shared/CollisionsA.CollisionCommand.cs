using CQRSharp.Abstractions.Data.Interfaces.Markers.Request;
using CQRSharp.Pipelines.Types.RateLimiting.Context;

namespace CQRSharp.Tests.Shared.CollisionsA;

public sealed class CollisionCommand : RequestBase<IRateLimitedContext>;

