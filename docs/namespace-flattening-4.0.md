# Namespace flattening — 4.0 plan

## Problem

The public surface is organized by *technical kind* (`Interfaces` vs `Models`, `Markers` vs `Handlers`) rather than by
task, so authoring a single handler pulls types from five namespaces:

| Type | Namespace |
| --- | --- |
| `CommandBase`, `ICommand` | `CQRSharp.Abstractions.Interfaces.Markers.Command` |
| `ICommandHandler<>` | `CQRSharp.Abstractions.Interfaces.Handlers` |
| `CommandResult` | `CQRSharp.Abstractions.Models.Commands` |
| `INotification` | `CQRSharp.Abstractions.Interfaces.Notifications` |
| `ICqrsDispatcher` | `CQRSharp.Core.Mediation` |

This is the #1 reported first-run friction. **3.1.0** mitigates it non-breakingly with shipped global usings
(`src/Application/CQRSharp/build/CQRSharp.props`, packed to `buildTransitive/`) and by moving `IRateLimitedContext` up
to `CQRSharp.Pipelines` (with an `[Obsolete]` alias left behind). **4.0** does the real fix.

## Target layout (4.0)

Collapse the authoring surface to **two** consumer-facing namespaces. Folders can stay exactly as they are — only the
`namespace` declarations move.

- **`CQRSharp`** — everything you touch to author and dispatch: `IRequest`/`ICommand`/`IQuery`/`IStreamRequest`,
  `CommandBase`/`QueryBase`/`StreamRequestBase`, `RequestBase<>`, the handler interfaces, `CommandResult`,
  `INotification`/`INotificationHandler`, `IRequestContext`/`RequestContextBase`, `RequestMetadata`, `ICqrsDispatcher`,
  `AddCqrs`/`AddCqrsGenerated`, validators, and the idempotency/retry markers.
- **`CQRSharp.Pipelines`** — opt-in behavior wiring: the fluent builder verbs (`UseValidation()`/`UseResilience(...)`/…),
  the behavior option types, `IRateLimitedContext`, `IUnitOfWork`, `IIdempotencyStore`, etc.

Genuinely internal/advanced types (diagnostics, telemetry, the generator/analyzer internals) stay in deeper
namespaces — they are not part of the first-run surface.

## Mechanics

1. Change `namespace` declarations only (folder tree unchanged); the move is mechanical and compiler-verified.
2. Remove the 3.1.0 `[Obsolete]` `IRateLimitedContext` shim.
3. Trim `build/CQRSharp.props` down to `<Using Include="CQRSharp" />` and `<Using Include="CQRSharp.Pipelines" />`.
4. Update the sample, tests, and README.
5. Watch for simple-name collisions when types from different old namespaces land in one namespace; rename or re-nest
   as needed, and run a full build to surface them.

## Why it is a major bump

Every consumer's `using` directives change. It is otherwise low-risk (no behavioral change), so it pairs well with any
other breaking changes queued for 4.0. Until then, the 3.1.0 global usings give consumers the same "no namespace
hunting" experience non-breakingly.
