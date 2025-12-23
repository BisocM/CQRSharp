using CQRSharp.Abstractions.Data.Interfaces.Markers.Request;
using CQRSharp.Pipelines.Types.RateLimiting.Context;

namespace CQRSharp.Tests.Shared.CollisionsB;

public sealed class CollisionCommand : RequestBase<IRateLimitedContext>;

