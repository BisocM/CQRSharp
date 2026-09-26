# Observability

CQRSharp emits **traces**, **metrics** and **logs** through the standard .NET abstractions (`ActivitySource`, `Meter`,
`ILogger`). Nothing is measured until a listener subscribes, and every log line is a source-generated message, so a
disabled level costs nothing.

- [Wiring it up](#wiring-it-up)
- [Distributed tracing](#distributed-tracing)
- [Trace propagation](#trace-propagation)
- [Attributes](#attributes)
- [Dispatch metrics](#dispatch-metrics)
- [Queue metrics](#queue-metrics)
- [Logging](#logging)

## Wiring it up

Every name CQRSharp emits under is published on `CqrsTelemetry` (namespace `CQRSharp.Core.Diagnostics`), so wiring
needs no string literals:

```csharp
using CQRSharp.Core.Diagnostics;

services.AddOpenTelemetry()
    .WithTracing(t => t.AddSource(CqrsTelemetry.ActivitySourceNames).AddOtlpExporter())
    .WithMetrics(m => m.AddMeter(CqrsTelemetry.MeterNames).AddOtlpExporter());
```

| Name | Constant | What it carries |
| --- | --- | --- |
| `CQRSharp` | `CqrsTelemetry.ActivitySourceName` | Activity source: dispatch, queued-execution and outbox-delivery spans. |
| `CQRSharp.Pipelines` | `CqrsTelemetry.PipelinesActivitySourceName` | Activity source: the built-in behaviors' spans. |
| `CQRSharp` | `CqrsTelemetry.MeterName` | Meter: [dispatch, notification and outbox instruments](#dispatch-metrics). |
| `CQRSharp.Core.BackgroundTasks` | `CqrsTelemetry.BackgroundTasksMeterName` | Meter: [background queue instruments](#queue-metrics). |

`CqrsTelemetry.ActivitySourceNames` and `CqrsTelemetry.MeterNames` list them all. The instrument names are constants on
`CqrsTelemetry.Instruments` and `CqrsTelemetry.QueueInstruments`, the attribute keys on `CqrsTelemetry.Tags`. The
`CQRSharp` activity source and meter report the version of the `CQRSharp.Core` assembly.

## Distributed tracing

With no listener subscribed, no span is started and an untraced, unmetered dispatch takes the allocation-free fast
path.

**`CQRSharp` activity source:**

| Span | Kind | Attributes | Notes |
| --- | --- | --- | --- |
| `CQRS Command <Type>` | Internal | `cqrsharp.request.type` | One per `Send` of a command, value-returning commands included. Status `Error` with the exception's message when it throws, and with the result's `ErrorMessage` when it **returns** a failed `CommandResult`; `Ok` otherwise. |
| `CQRS Query <Type>` | Internal | `cqrsharp.request.type` | One per `Send` of a query; status as above. |
| `CQRS Stream <Type>` | Internal | `cqrsharp.request.type` | One per enumeration of a stream. It is current on every step of the stream, so per-item child spans (EF Core, HTTP) and outbox trace parents attach to it. Status `Error` when the stream throws, and when its consumer stops enumerating early. |
| `CQRS Queued <Type>` | Internal | `cqrsharp.request.type` | A `RunMode.Queued` dispatch, parented to the caller's span; the request's own span runs under it. |
| `CQRS Outbox Dispatch` | Consumer | `cqrsharp.notification.name`, `cqrsharp.notification.handler`, `cqrsharp.partition_key` (when partitioned) | One per outbox message delivered to its handler, parented to the span that published it. Status `Error` for a failed attempt or a dead letter. |

`<Type>` is the request type's short name (`Type.Name`); the attribute carries the full name. A traced `Send` does not
leave its span as the caller's `Activity.Current`, so consecutive sends are siblings.

**`CQRSharp.Pipelines` activity source** (every span carries `cqrsharp.request.type`):

| Span | Started for | Attributes and events |
| --- | --- | --- |
| `RateLimiting.Check` | a request whose context implements `IRateLimitedContext` | `cqrsharp.ratelimit.user_id`; on a rejection, status `Error` and `cqrsharp.ratelimit.retry_after_ms`. It covers the check only and ends before the rest of the pipeline runs. |
| `Resilience.Operation` | an `IRetryableRequest` | `cqrsharp.resilience.max_retries`; `cqrsharp.resilience.retry_delay_ms` (the latest back-off); a `RetryAttempt-<n>` event per retry, with an `exception.type` attribute. Status `Error` with the reason when a failure is not retried or retries are exhausted. |
| `UoW.Transaction` | an `ITransactionalCommand` / `ITransactionalQuery` | `cqrsharp.transaction.isolation_level`; events `Transaction Started`, `Transaction Committed`, `Transaction Rolled Back`, `Participating in existing transaction` and `Outbox Store Failed`. Status `Error` with the reason for a rollback. |
| `Timeout.Guard` | every request, once `UseTimeout` is on | `cqrsharp.timeout_ms`; status `Error` when the request timed out. |

The spans of a streaming request's behaviors are current on every step of the stream, like the stream span.

## Trace propagation

CQRSharp keeps the trace across its asynchronous boundaries:

- A **queued** dispatch (`RunMode.Queued`) captures the caller's span and parents the `CQRS Queued` span to it, so work
  that runs on the background queue still belongs to the request that scheduled it.
- The **outbox** stores the W3C `traceparent` of the span that published a message (`OutboxMessage.TraceParent`: the
  request's span when a handler published it). The processor parents the `CQRS Outbox Dispatch` span to it, so the
  delivery belongs to the original trace even when it runs minutes later in another process.

## Attributes

A key means the same thing, with the same value, wherever it appears.

| Key | Constant (`CqrsTelemetry.Tags`) | Value | Where |
| --- | --- | --- | --- |
| `cqrsharp.request.type` | `RequestType` | The request type's full name, generic arguments included, without assembly names (`Type.ToString()`, e.g. `Shop.Orders.CreateOrder+Command`) | Every dispatch and behavior span; `cqrsharp.request.duration` |
| `cqrsharp.request.kind` | `RequestKind` | `command`, `query` or `stream` | `cqrsharp.request.duration` |
| `cqrsharp.outcome` | `Outcome` | Listed with each instrument | `cqrsharp.request.duration`, the outbox instruments |
| `cqrsharp.notification.type` | `NotificationType` | The notification type's full name, in the form of `cqrsharp.request.type` | `cqrsharp.notifications.published` |
| `cqrsharp.notification.name` | `NotificationName` | The stable name the message is stored under (`[NotificationName]`) | Outbox dispatch span, outbox instruments |
| `cqrsharp.notification.handler` | `NotificationHandler` | The stable name of the handler the message is addressed to | Outbox dispatch span, outbox instruments |
| `cqrsharp.partition_key` | `PartitionKey` | The message's partition key | Outbox dispatch span, when partitioned |
| `cqrsharp.transaction.isolation_level` | `IsolationLevel` | The isolation level the transaction began with | `UoW.Transaction` |
| `cqrsharp.timeout_ms` | `TimeoutMilliseconds` | The request's time budget, in milliseconds | `Timeout.Guard` |
| `cqrsharp.resilience.max_retries` | `MaxRetries` | The most retries the request may get | `Resilience.Operation` |
| `cqrsharp.resilience.retry_delay_ms` | `RetryDelayMilliseconds` | The back-off before the latest retry, in milliseconds | `Resilience.Operation` |
| `cqrsharp.ratelimit.user_id` | `RateLimitUserId` | `IRateLimitedContext.UserId` | `RateLimiting.Check` |
| `cqrsharp.ratelimit.retry_after_ms` | `RateLimitRetryAfterMilliseconds` | The wait for the caller's next token, in whole milliseconds, rounded up | `RateLimiting.Check` of a rejected request |
| `cqrsharp.queue.reason` | `QueueReason` | Why the background queue evicted or refused a work item | `cqrsharp.queue.evicted`, `cqrsharp.queue.rejected` |

## Dispatch metrics

The **`CQRSharp`** meter (`CqrsTelemetry.MeterName`) carries these instruments (`CqrsTelemetry.Instruments`):

| Instrument | Kind (unit) | Tags |
| --- | --- | --- |
| `cqrsharp.request.duration` | Histogram (`s`): a command, query or stream end to end, behaviors included | `cqrsharp.request.type`, `cqrsharp.request.kind`, `cqrsharp.outcome` (`success` / `failure`) |
| `cqrsharp.notifications.published` | Counter (`{notification}`): notifications published in-process | `cqrsharp.notification.type` |
| `cqrsharp.outbox.messages` | Counter (`{message}`): outbox messages the processor finished an attempt at | `cqrsharp.notification.name`, `cqrsharp.notification.handler`, `cqrsharp.outcome` (see below) |
| `cqrsharp.outbox.dispatch.duration` | Histogram (`s`): dispatching one outbox message to its handler | same as `cqrsharp.outbox.messages` |
| `cqrsharp.outbox.pending` | Gauge (`{message}`): messages still to be delivered, pending or in progress | — |
| `cqrsharp.outbox.dead_letters` | Gauge (`{message}`): dead-lettered messages awaiting an operator | — |
| `cqrsharp.outbox.lag` | Gauge (`s`): the age of the oldest undelivered message | — |

The outbox outcomes are:

| `cqrsharp.outcome` | Meaning |
| --- | --- |
| `processed` | Delivered and marked processed. |
| `duplicate` | The inbox recognised a redelivery; the handler did not run again. |
| `unrecorded` | Delivered, but marking it processed failed; it is delivered again once its lease runs out. |
| `retry` | The handler failed; the attempt was recorded and the message backs off. |
| `deferred` | This instance does not know the notification or the handler; handed back for another instance without counting an attempt. |
| `dead_letter` | Dead-lettered. |
| `claim_lost` | The lease ran out before the message was dispatched, or before its outcome was recorded; another processor holds it now. |
| `not_started` | A step before the handler (renewing the lease, checking the inbox, beginning the delivery's transaction) failed; no attempt was charged, and the message is claimable again once its lease runs out. |

Request *counts* and *error rates* come from the duration histogram: its count, split by `cqrsharp.outcome`. A command
that **returns** a failed `CommandResult` is a `failure`, and so is a stream its consumer stops early or that faults.
`dead_letter` and a rising `retry` rate on the outbox counter, and the `lag` and `dead_letters` gauges, are the ones
worth alerting on. The gauges report the processor's last backlog sample, taken every
`OutboxProcessorOptions.BacklogSampleInterval` while a listener is attached; the outbox health check reads the backlog
live (see [The outbox](outbox.md#backlog-gauges-and-the-health-check)).

Each service provider owns its own `CQRSharp` meter, created through the provider's `IMeterFactory` when one is
registered (the Generic Host registers one). Several hosts in one process therefore publish the same instrument names,
each reporting its own requests and its own outbox; tell them apart by resource attributes. A stopping host stops
reporting its backlog without touching another host's.

## Queue metrics

Every background task queue (one per host; it backs `RunMode.Queued` and `IBackgroundTaskManager`) has its own meter
named **`CQRSharp.Core.BackgroundTasks`** (`CqrsTelemetry.BackgroundTasksMeterName`, already in
`CqrsTelemetry.MeterNames`), created through the provider's `IMeterFactory` when one is registered, like the `CQRSharp`
meter, with these instruments (`CqrsTelemetry.QueueInstruments`):

| Instrument | Kind (unit) | Meaning |
| --- | --- | --- |
| `cqrsharp.queue.depth` | Observable up-down counter (`{item}`) | Work items waiting in the queue: accepted, not started yet. A withdrawn item keeps its place until the consumer reaches it. |
| `cqrsharp.queue.enqueued` | Counter (`{item}`) | Work items the queue accepted. |
| `cqrsharp.queue.evicted` | Counter (`{item}`) | Queued items evicted from a full queue to make room for newer work. `cqrsharp.queue.reason`: `drop_oldest` / `drop_newest`, the full mode that evicted it. |
| `cqrsharp.queue.rejected` | Counter (`{item}`) | Work items the queue refused. `cqrsharp.queue.reason`: `full` (the queue was full and its full mode is `DropWrite`) or `closed` (the queue no longer accepts work). |
| `cqrsharp.queue.wait.duration` | Histogram (`s`) | How long a work item waited in the queue before it started. |

There is nothing to switch on: the queue measures only while a listener is attached.

## Logging

Every log line CQRSharp writes is a source-generated `LoggerMessage` with a stable event id, so a filter or an alert can
target it. Each component has its own block of ids; ids are unique within an assembly, and the logger category (the
component's type, under `CQRSharp.*`) tells the assemblies apart. Properties keep one name per datum: `{RequestName}`
(the request type's short name), `{NotificationName}` (a notification's type name), `{NotificationType}` (the stable
outbox name), `{MessageId}`, `{HandlerName}`, `{UserId}`, `{ElapsedMs}`, `{Attempt}` / `{MaxAttempts}`,
`{ExceptionType}` and `{Code}`.

**Levels follow who has to act.** The logging behavior (`UseLogging()`) is the one place a failed request is logged at
Error with its exception. Outcomes the caller has to fix, and decisions other behaviors take, are one line below
Warning, without a stack trace. Warning is kept for failures that are swallowed and repaired later and for requests the
server could not serve; Error for what needs an operator. A host that keeps `CQRSharp` at Warning therefore sees
swallowed failures, requests it could not serve, and everything at Error, but not validation rejections or a failed
`CommandResult`.

The logging behavior runs inside exception handling and rate limiting (a throttled request is not logged by it) and
outside validation, resilience, idempotency, the unit of work and the timeout, so its timing includes retries. Elapsed
time is measured with the injected `TimeProvider`. It sorts every failure:

| Outcome | Request | Stream | Level |
| --- | --- | --- | --- |
| Canceled by the caller | 4006 | 4007 | Information |
| Rejected: `RequestValidationException`, `DuplicateRequestException`, `IdempotencyKeyMismatchException`, `RateLimitExceededException` | 4008 | 4010 | Information |
| Not served: `RequestTimeoutException`, `BackgroundTaskRejectedException` | 4009 | 4011 | Warning |
| Any other exception | 4002 | 4005 | Error, with the exception |

### Event ids

**Dispatch** (`CQRSharp.Core.Pipelines.PipelineExecutor`): failures the failure path must swallow.

| Id | Level | Event |
| --- | --- | --- |
| 1000 | Warning | A `*Failed` lifecycle-notification subscriber failed; the request's own failure is what its caller receives. |
| 1001 | Warning | A post-handler failed after the request had already failed. |
| 1002 | Warning | Disposing a stream that had already failed failed as well. |

**Background task queue** (`CQRSharp.Core.BackgroundTasks.BackgroundTaskQueueConsumer`).

| Id | Level | Event |
| --- | --- | --- |
| 1100 | Debug | The consumer started, with its concurrency. |
| 1101 | Debug | The consumer is stopping. |
| 1102 | Critical | The consumer loop failed; the queue no longer accepts work. |
| 1103 | Information | Waiting for running work items to finish at shutdown. |
| 1104 | Warning | The shutdown budget (`ShutdownTimeout`) ran out; canceling the work still running. |
| 1105 | Error | Shutting the consumer down failed. |
| 1106 | Warning | Queued work items that did not get to run before shutdown were canceled. |
| 1107 | Error | A work item could not hand its outcome to its caller. |
| 1108 | Debug | The consumer stopped. |

**Startup validation** (`CQRSharp.Core.Diagnostics.CqrsStartupValidator`); see [Diagnostics](diagnostics.md#startup-validation-cqrconf).

| Id | Level | Event |
| --- | --- | --- |
| 1200 | Error | A validation error: `{Code}`, `{Message}`. |
| 1201 | Warning | A validation warning: `{Code}`, `{Message}`. |
| 1202 | Information | Validation passed with no issues. |
| 1203 | Information | The summary: error and warning counts, and the policy. |

**First-use configuration checks** (category `CQRSharp.Core.Diagnostics.CqrsConfiguration`); see
[Diagnostics](diagnostics.md#first-use-checks). Each is logged once per service provider and type.

| Id | Level | Event |
| --- | --- | --- |
| 1204 | Warning | A request's first dispatch met a configuration warning (`CQRCONF006`): `{Code}`, `{RequestName}`, `{Message}`. |
| 1205 | Warning | A notification's first publish under the outbox met a configuration warning (`CQRCONF003`, `CQRCONF011`): `{Code}`, `{NotificationName}`, `{Message}`. |
| 1206 | Error | A notification's first publish to the outbox met a configuration error that does not fail it (`CQRCONF012`): `{Code}`, `{NotificationName}`, `{Message}`. |

**Logging behavior** (`CQRSharp.Pipelines.LoggingBehavior` / `StreamLoggingBehavior`).

| Id | Level | Event |
| --- | --- | --- |
| 4000 | Information | Handling a request. |
| 4001 | Information | Handled a request, with `{ElapsedMs}`. |
| 4002 | Error | A request failed, with the exception. |
| 4003 | Information | Streaming a request. |
| 4004 | Information | Streamed a request: item count and `{ElapsedMs}`. |
| 4005 | Error | A stream failed, with the exception. |
| 4006 | Information | A request was canceled by the caller. |
| 4007 | Information | A stream was canceled by the caller. |
| 4008 | Information | A request was rejected, with `{ExceptionType}`. |
| 4009 | Warning | A request could not be served, with `{ExceptionType}`. |
| 4010 | Information | A stream was rejected, with `{ExceptionType}`. |
| 4011 | Warning | A stream could not be served, with `{ExceptionType}`. |

**Idempotency** (`CQRSharp.Pipelines.IdempotencyBehavior` / `StreamIdempotencyBehavior`).

| Id | Level | Event |
| --- | --- | --- |
| 4100 | Debug | Replayed the stored result of a duplicate. |
| 4101 | Debug | Rejected a duplicate request. |
| 4102 | Information | Rejected a request: its key was used with a different payload. |
| 4103 | Warning | Could not record a completed result; a duplicate will be rejected rather than replayed. |
| 4104 | Warning | Could not release the key of a failed request; a retry is rejected until the claim expires. |
| 4105 | Debug | Rejected a duplicate streaming request. |
| 4106 | Information | Rejected a streaming request: its key was used with a different payload. |
| 4107 | Warning | Could not record a completed streaming request. |
| 4108 | Warning | Could not release the key of a failed streaming request. |
| 4109 | Warning | The result serializer `{SerializerName}` cannot store a request's result, so its duplicates are rejected; once per request type and serializer instance. |
| 4110 | Warning | Disposing a streaming request's stream, which had already failed, failed as well; the stream's own failure is what its consumer receives. |

**Unit of work** (`CQRSharp.Pipelines.UnitOfWorkBehavior` / `StreamUnitOfWorkBehavior`).

| Id | Level | Event |
| --- | --- | --- |
| 4200 | Trace | Taking part in an existing transaction. |
| 4201 | Information | The request failed with `{ExceptionType}`; rolling back. |
| 4202 | Information | A stream that writes was not read to its end; rolling back its transaction and its work. |
| 4203 | Information | The request returned a failed result; rolling back. |
| 4204 | Information | The commit failed with `{ExceptionType}`; rolling back. |
| 4205 | Error | The rollback failed; the failure that caused it is the one reported. |
| 4206 | Error | Committed, but storing the request's outbox notifications failed; they are lost (types listed). |
| 4207 | Debug | Canceled by the caller; rolling back. |
| 4208 | Debug | A read-only stream was not read to its end; rolling back. |
| 4209 | Warning | Disposing a transactional stream, which had already failed, failed as well; the stream's own failure is what its consumer receives. |

**Resilience** (`CQRSharp.Pipelines.ResilienceBehavior` / `StreamResilienceBehavior`).

| Id | Level | Event |
| --- | --- | --- |
| 4300 | Warning | A request failed and is retried: `{Attempt}` of `{MaxRetries}`, with the delay and the exception. |
| 4301 | Information | A request failed on every attempt; not retrying further. |
| 4302 | Debug | A request failed with an outcome that is never retried, with the reason. |
| 4303 | Information | A stream failed after yielding items; not retried, so no item is delivered twice. |

**Timeout** (`CQRSharp.Pipelines.TimeoutBehavior` / `StreamTimeoutBehavior`).

| Id | Level | Event |
| --- | --- | --- |
| 4400 | Information | A request timed out, with `{TimeoutMs}`. |
| 4401 | Information | A stream timed out, with `{TimeoutMs}`. |

**Rate limiting** (`CQRSharp.Pipelines.RateLimitingBehavior` / `StreamRateLimitingBehavior`).

| Id | Level | Event |
| --- | --- | --- |
| 4500 | Information | Rate limited a request for `{UserId}`; a token is available again in `{RetryAfterMs}` (whole milliseconds). |

**Exception handling** (`CQRSharp.Pipelines.ExceptionHandlingBehavior` / `StreamExceptionHandlingBehavior`).

| Id | Level | Event |
| --- | --- | --- |
| 4600 | Information | A request failed with `{ExceptionType}`; an exception handler supplied its result. |
| 4601 | Information | A stream failed with `{ExceptionType}`; an exception handler supplied the rest of the stream. |

**Outbox processor** (`CQRSharp.Core.Outbox.OutboxProcessor`).

| Id | Level | Event |
| --- | --- | --- |
| 5000 | Information | The processor is starting. |
| 5001 | Information | The processor is stopping. |
| 5002 | Error | An unhandled exception in the processor. |
| 5003 | Warning | The outbox services are not registered; the processor does not run. |
| 5004 | Debug | Fetched a batch of messages. |
| 5006 | Warning | The claim on a message was lost before dispatch: the visibility timeout is too short for the batch. |
| 5007 | Error | A notification no instance knew within `{GracePeriod}` was dead-lettered. |
| 5008 | Error | A message for a handler no instance has within `{GracePeriod}` was dead-lettered. |
| 5009 | Debug | Delivered a message to its handler. |
| 5010 | Debug | Skipped a redelivery the inbox recognised. |
| 5011 | Warning | Delivered a message whose claim had been lost; it may be delivered again. |
| 5012 | Error | A payload cannot be read; dead-lettering the message. |
| 5013 | Warning | Dead-lettering a message failed; it is claimable again once its lease runs out. |
| 5014 | Warning | A handler failed on `{Attempt}` of `{MaxAttempts}`, with the exception. |
| 5015 | Error | A handler failed on its last attempt; the message is dead-lettered. |
| 5016 | Warning | Recording a failed attempt failed; the message is claimable again once its lease runs out. |
| 5017 | Information | Released the claimed messages of the batch on shutdown. |
| 5018 | Warning | Releasing the claimed messages on shutdown failed. |
| 5019 | Warning | Measuring the backlog failed; the gauges keep their last sample. |
| 5020 | Error | Rolling back a delivery's transaction failed. |
| 5021 | Debug | The processor is idle: the outbox mode is `Disabled`. |
| 5022 | Warning | Delivered, but marking the message processed failed; it will be delivered again once its lease runs out. |
| 5023 | Warning | Delivered, but recording the delivery in the inbox failed; the delivery stands. |
| 5024 | Information | A notification unknown to this instance was deferred until `{NotBefore}`. |
| 5025 | Information | A message for a handler this instance does not have was deferred until `{NotBefore}`. |
| 5026 | Warning | Deferring a message failed; it is claimable again once its lease runs out. |
| 5027 | Error | A delivery committed, but storing the notifications its handler published failed; they are lost. |
| 5028 | Warning | A step before the handler (renewing the claim, checking the inbox, beginning the delivery's transaction) failed; no attempt is charged, and the message is claimable again once its lease runs out. |

**EF Core stores** (`CQRSharp.EntityFrameworkCore.*`).

| Id | Level | Event |
| --- | --- | --- |
| 6000 | Debug | An outbox claim lost a concurrency race; retrying. |
| 6001 | Debug | An outbox claim lost `MaxClaimAttempts` races in a row; the contested messages wait for the next poll. |
| 6002 | Debug | Handed a message back right after claiming it: another processor claimed a message of its partition at the same time. |
| 6010 | Information | Purged processed outbox messages. |
| 6011 | Information | Purged dead letters. |
| 6012 | Information | Purged inbox records. |
| 6013 | Warning | Purging the outbox failed; tried again an interval later. |
| 6020 | Debug | An idempotency claim lost an insert race; retrying. |
| 6021 | Debug | An idempotency take-over lost a concurrency race; retrying. |
| 6030 | Information | Purged expired idempotency keys. |
| 6031 | Warning | Purging expired idempotency keys failed; tried again an interval later. |

**ASP.NET Core** (`CQRSharp.AspNetCore.CqrsExceptionHandler`); see [ASP.NET Core](aspnetcore.md#logging).

| Id | Level | Event |
| --- | --- | --- |
| 7000 | Information | Mapped an exception the caller has to act on to a ProblemDetails response. |
| 7001 | Warning | Mapped `RequestTimeoutException` or `BackgroundTaskRejectedException`: the server could not serve the request. |

The Redis stores write no logs of their own.
