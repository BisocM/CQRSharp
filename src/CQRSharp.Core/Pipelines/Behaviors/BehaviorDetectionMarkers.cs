namespace CQRSharp.Core.Pipelines;

// Marker interfaces implemented by the optional pipeline behaviors so the diagnostics can tell whether what a request
// opts into (IIdempotentRequest, IRetryableRequest) or brings along (validators, exception hooks) is actually honored by
// a wired behavior — without Core taking a compile-time dependency on the concrete behavior types in CQRSharp.Pipelines.
// Internal: visible to the behaviors via InternalsVisibleTo(CQRSharp.Pipelines), and used only by the diagnostics.

/// <summary>Implemented by the idempotency pipeline behavior so inspection can confirm idempotency is wired.</summary>
internal interface ICqrsIdempotencyBehaviorMarker;

/// <summary>Implemented by the resilience/retry pipeline behavior so inspection can confirm retries are wired.</summary>
internal interface ICqrsResilienceBehaviorMarker;

/// <summary>Implemented by the validation behaviors so the diagnostics can confirm a request's validators run.</summary>
internal interface ICqrsValidationBehaviorMarker;

/// <summary>Implemented by the exception-handling behaviors so the diagnostics can confirm a request's exception hooks run.</summary>
internal interface ICqrsExceptionHandlingBehaviorMarker;
