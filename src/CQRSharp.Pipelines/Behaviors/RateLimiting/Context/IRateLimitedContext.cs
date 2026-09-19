using System;

namespace CQRSharp.Pipelines.Behaviors.RateLimiting.Context;

/// <summary>
///     Backwards-compatibility shim. <c>IRateLimitedContext</c> moved to the top-level <c>CQRSharp.Pipelines</c>
///     namespace in 3.1.0; this alias keeps existing implementations compiling and is removed in 4.0. Update your
///     <c>using</c> to <c>CQRSharp.Pipelines</c>.
/// </summary>
[Obsolete("IRateLimitedContext moved to the CQRSharp.Pipelines namespace. Change the using to 'CQRSharp.Pipelines'; this alias is removed in 4.0.")]
public interface IRateLimitedContext : CQRSharp.Pipelines.IRateLimitedContext;
