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
