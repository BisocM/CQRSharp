# Observability

CQRSharp is instrumented for **distributed tracing**, **structured logging**, and **queue metrics** out
of the box. Tracing has zero overhead when no listener is attached.

- [Distributed tracing](#distributed-tracing)
- [Trace propagation](#trace-propagation)
- [Logging](#logging)
- [Queue metrics](#queue-metrics)

## Distributed tracing

Every name CQRSharp emits under is published on `CqrsTelemetry`, so wiring needs no string literals:

```csharp
services.AddOpenTelemetry()
    .WithTracing(t => t.AddSource(CqrsTelemetry.ActivitySourceNames).AddOtlpExporter())
    .WithMetrics(m => m.AddMeter(CqrsTelemetry.MeterNames).AddOtlpExporter());
```

Dispatch, queued-execution and outbox spans come from the `ActivitySource` named **`CQRSharp`**
(`CqrsTelemetry.ActivitySourceName`); the opt-in behaviors' spans from **`CQRSharp.Pipelines`**. Telemetry is
pay-for-use: with no listener subscribed nothing is measured, and an untraced, unmetered dispatch takes the
allocation-free fast path.

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

## Dispatch metrics

The **`CQRSharp`** meter (`CqrsTelemetry.MeterName`) carries:

| Instrument | Kind | Tags |
| --- | --- | --- |
| `cqrsharp.request.duration` | Histogram (s) — a command, query or stream end to end, behaviors included | `cqrsharp.request.type`, `cqrsharp.request.kind` (`command` / `query` / `stream`), `cqrsharp.outcome` (`success` / `failure`) |
| `cqrsharp.notifications.published` | Counter — notifications published in-process | `cqrsharp.notification.type` |
| `cqrsharp.outbox.messages` | Counter — outbox messages the processor finished with | `cqrsharp.notification.type`, `cqrsharp.outcome` (`processed` / `retry` / `dead_letter` / `claim_lost`) |
| `cqrsharp.outbox.dispatch.duration` | Histogram (s) — dispatching one outbox message | same as above |

Request *counts* and *error rates* come from the duration histogram (its count, split by `cqrsharp.outcome`). A
command that **returns** a failed `CommandResult` is recorded as a `failure`, and so is a stream its consumer
abandons before the end. `dead_letter` and a rising `retry` rate on the outbox counter are the two worth alerting on.

## Queue metrics

The background task queue (used by `RunMode.Queued`) reports operational metrics through
`IQueueMetricsReporter`. The built-in `OpenTelemetryQueueMetricsReporter` publishes them as OpenTelemetry
metrics; register it to surface queue depth, throughput, and rejections (back-pressure) on your metrics
pipeline. Implement `IQueueMetricsReporter` yourself to route the same signals to a different metrics
backend.

They are published on the **`CQRSharp.Core.BackgroundTasks`** meter (`CqrsTelemetry.BackgroundTasksMeterName`),
which `CqrsTelemetry.MeterNames` already includes.

Pair these with the [diagnostics introspection API and health check](diagnostics.md) for a complete
picture: tracing and metrics for *runtime* behavior, the bindings health check and validator for
*configuration* health.
