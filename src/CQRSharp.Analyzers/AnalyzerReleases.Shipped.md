; Shipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md
; Tracking starts with 4.2.1, the last 4.x release: its table lists every rule that release shipped (CHANGELOG.md
; records the release that introduced each). A removed rule's ID stays reserved and is never reused.

## Release 4.2.1

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
CQRA001 | CQRSharp.Usage | Warning | A handler's context type does not match the context its request declares.
CQRA003 | CQRSharp.Usage | Warning | A dispatched request has no discoverable handler.
CQRA004 | CQRSharp.Usage | Error | A stream request must be dispatched with Stream(...), not Send(...).
CQRA005 | CQRSharp.Usage | Error | A PipelineExemption target that is not a pipeline behavior has no effect.
CQRA006 | CQRSharp.Usage | Info | A published notification has no discoverable subscriber.
CQRA007 | CQRSharp.Usage | Warning | A request's context does not implement IRateLimitedContext while rate limiting is configured.
CQRA008 | CQRSharp.Usage | Info | A closed-generic PipelineExemption can use the simpler open-generic form.
CQRA009 | CQRSharp.Usage | Info | A value-returning command (ICommand&lt;TResult&gt;) — confirm the value cannot be queried.
CQRA010 | CQRSharp.Usage | Warning | CQRSharp handlers found but the source generator is not running in this project.
CQRA011 | CQRSharp.Usage | Warning | A custom-context request has no discoverable IRequestContextFactory.
CQRA012 | CQRSharp.Usage | Warning | An IRequestValidator is declared but the validation behavior is not enabled.
CQRA013 | CQRSharp.Usage | Info | A request does not opt into a configured resilience/idempotency behavior.
CQRA014 | CQRSharp.Usage | Error | AddCqrs() is called directly; use AddCqrsGenerated() to also wire generated routing.
CQRA017 | CQRSharp.Usage | Info | A pre+post handler attribute pair can be a single ICommandInterceptor.

## Release 5.0.0

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
CQRA015 | CQRSharp.Usage | Warning | ITransactionalCommand is implemented by a request that is not a command.
CQRA018 | CQRSharp.Usage | Error | An IIdempotentRequest is handled, but the configuration in view never calls UseIdempotency.
CQRA019 | CQRSharp.Usage | Warning | An IRetryableRequest is handled, but the configuration in view never calls UseResilience.
CQRA020 | CQRSharp.Usage | Info | A handled notification without [NotificationName] is not durable while the outbox is on.

### Removed Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
CQRA001 | CQRSharp.Usage | Warning | Handler interfaces no longer take a context type argument; the request carries its typed context.
CQRA007 | CQRSharp.Usage | Warning | Rate limiting is opt-in per request, so a request without IRateLimitedContext is not a mistake.
CQRA009 | CQRSharp.Usage | Info | Fired on every value-returning command, including the correct ones; the guidance lives on ICommand&lt;TResult&gt;.
CQRA013 | CQRSharp.Usage | Info | Retries and idempotency are opt-in per request; CQRCONF005/006 report a marker whose behavior is not wired.
CQRA017 | CQRSharp.Usage | Info | ICommandInterceptor is removed; an attribute implements IPreHandlerAttribute and IPostHandlerAttribute.
