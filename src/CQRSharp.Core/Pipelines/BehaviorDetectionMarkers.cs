namespace CQRSharp.Core.Pipelines;

// Marker interfaces implemented by the optional pipeline behaviors so configuration inspection can detect whether a
// request-level marker (IIdempotentRequest / IRetryableRequest) is actually honored by a wired behavior — without Core
// taking a compile-time dependency on the concrete behavior types in CQRSharp.Pipelines. Internal: visible to the
// behaviors via InternalsVisibleTo(CQRSharp.Pipelines), and used only inside the configuration inspector.

/// <summary>Implemented by the idempotency pipeline behavior so inspection can confirm idempotency is wired.</summary>
internal interface ICqrsIdempotencyBehaviorMarker;

/// <summary>Implemented by the resilience/retry pipeline behavior so inspection can confirm retries are wired.</summary>
internal interface ICqrsResilienceBehaviorMarker;
