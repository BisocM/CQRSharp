# Observability

CQRSharp is instrumented for **distributed tracing**, **structured logging**, and **queue metrics** out
of the box. Tracing has zero overhead when no listener is attached.

- [Distributed tracing](#distributed-tracing)
- [Trace propagation](#trace-propagation)
- [Logging](#logging)
- [Queue metrics](#queue-metrics)

## Distributed tracing

All spans are emitted from a single `ActivitySource` named **`CQRSharp`**, exposed as
`CqrsActivitySource.Name`. Subscribe to it to collect spans — for example with OpenTelemetry:

```csharp
services.AddOpenTelemetry().WithTracing(t => t
    .AddSource(CqrsActivitySource.Name)   // "CQRSharp"
    .AddOtlpExporter());
```

Spans are produced for:

- **Request dispatch** — one span per `Send`/`Stream`, named `"<operation> <RequestType>"`, tagged with
  `cqrsharp.request_type`.
- **Queued execution** — when `RunMode.Queued` is used, a span around the queued work, linked to the
  originating request's trace.
- **Pipeline behaviors** — the built-in behaviors emit their own activities (e.g. the unit-of-work
  transaction span with `db.isolation_level`, the resilience operation span with retry counts) via
  `PipelineTelemetry`, so a slow transaction or a retry storm is visible in the trace.
- **Outbox delivery** — each outbox dispatch span links back to the request that produced the message.

When no listener is subscribed, `StartActivity` returns `null` and the instrumentation is effectively
free.

## Trace propagation

CQRSharp preserves trace context across its asynchronous boundaries:

- A **queued** dispatch (`RunMode.Queued`) captures the caller's `Activity.Current` context and parents
  the queued execution's spans to it, so work that runs on the background queue still links to the
  request that scheduled it.
- The **outbox** captures the W3C `traceparent` of the request that produced a message
  (`OutboxMessage.TraceParent`) and uses it when the processor later delivers the notification — so the
  delivery span links back to the original trace even though it runs minutes later in another process.

## Logging

Enable the logging behavior with `UseLogging()`. It logs the start, completion (with elapsed time), and
failure of each request and streaming request through the standard `ILogger<T>` abstraction, so it flows
into whatever logging provider your host uses. Elapsed time is measured with the injected `TimeProvider`
(`GetElapsedTime`), making it deterministic in tests. Logging runs outermost in the pipeline, so its
timing includes retries and all inner behaviors.

The framework's own components (the outbox processor, the background queue consumer, the startup
validator) also log through `ILogger`, with the validator emitting each `CQRCONF` issue at its severity.

## Queue metrics

The background task queue (used by `RunMode.Queued`) reports operational metrics through
`IQueueMetricsReporter`. The built-in `OpenTelemetryQueueMetricsReporter` publishes them as OpenTelemetry
metrics; register it to surface queue depth, throughput, and rejections (back-pressure) on your metrics
pipeline. Implement `IQueueMetricsReporter` yourself to route the same signals to a different metrics
backend.

```csharp
services.AddOpenTelemetry().WithMetrics(m => m.AddMeter(/* CQRSharp queue meter */));
```

Pair these with the [diagnostics introspection API and health check](diagnostics.md) for a complete
picture: tracing and metrics for *runtime* behavior, the bindings health check and validator for
*configuration* health.
