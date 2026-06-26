using CQRSharp.Abstractions.Interfaces.Markers.Request;
using CQRSharp.Pipelines;

namespace CQRSharp.Tests.Shared.CollisionsB;

public sealed class CollisionCommand : RequestBase<IRateLimitedContext>;