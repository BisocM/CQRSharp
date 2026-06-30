; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

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
