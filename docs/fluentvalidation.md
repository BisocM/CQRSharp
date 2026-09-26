# FluentValidation

```bash
dotnet add package CQRSharp.FluentValidation
```

`CQRSharp.FluentValidation` lets a codebase that already has FluentValidation `AbstractValidator<T>`
classes use them as CQRSharp request validators with one line of setup. It adds no pipeline behavior of
its own: it plugs an adapter into the existing [validation behavior](pipeline-behaviors.md#validation),
so ordering, the thrown exception and everything else documented there stay the same.

- [Native AOT](#native-aot)
- [Setup](#setup)
- [What happens at runtime](#what-happens-at-runtime)
- [How failures are mapped](#how-failures-are-mapped)
- [Severity](#severity)
- [Mixing with native validators](#mixing-with-native-validators)
- [Registering without the builder](#registering-without-the-builder)

## Native AOT

> **This package is not Native-AOT or full-trim compatible.** FluentValidation builds its rules from
> expression trees that it compiles at runtime. Like the EF Core integration, the package does not set
> `IsAotCompatible`, and its two registration verbs are annotated with `[RequiresDynamicCode]` /
> `[RequiresUnreferencedCode]` so an AOT or trimmed app gets an analyzer warning at the call site. The
> rest of CQRSharp stays AOT-clean; if you publish with Native AOT, write native
> `IRequestValidator<TRequest>` implementations instead.

## Setup

Write validators exactly as you would for any FluentValidation project. The validated type is the
request:

```csharp
using CQRSharp;
using FluentValidation;

public sealed class CreateUser : CommandBase
{
    public string Name { get; init; } = string.Empty;
    public int Age { get; init; }
}

public sealed class CreateUserValidator : AbstractValidator<CreateUser>
{
    public CreateUserValidator()
    {
        RuleFor(c => c.Name).NotEmpty().WithErrorCode("NAME_REQUIRED");
        RuleFor(c => c.Age).GreaterThanOrEqualTo(18);
    }
}
```

Then turn the integration on and register your validators:

```csharp
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;

services.AddCqrsGenerated(b => b.UseFluentValidation());
services.AddValidatorsFromAssemblyContaining<CreateUserValidator>();
```

`UseFluentValidation()` registers the adapter. The validation behavior that runs it is already part of every
`AddCqrsGenerated(b => ...)` registration unless `UseValidation(false)` turns it off, so nothing else needs enabling.
`UseValidation(false)` turns FluentValidation validators off together with every other validator, whatever the order of
the two calls.

The integration **never scans assemblies itself**. Registering the validators is FluentValidation's job:
`AddValidatorsFromAssemblyContaining<T>()` / `AddValidatorsFromAssembly(...)` come from the separate
[`FluentValidation.DependencyInjectionExtensions`](https://www.nuget.org/packages/FluentValidation.DependencyInjectionExtensions)
package (scoped lifetime by default, which works here). You can equally register them by hand:

```csharp
services.AddScoped<IValidator<CreateUser>, CreateUserValidator>();
```

Any lifetime works. The adapter is transient and is resolved from the same scope as the request's
pipeline, so scoped validators (and validators with scoped dependencies such as a `DbContext`) are safe.

## What happens at runtime

The validation behavior runs every `IRequestValidator<TRequest>` of the request being dispatched: the ones the source
generator discovered and the ones registered in the container. The integration registers one open-generic entry among
them, `FluentValidationRequestValidator<TRequest>` (namespace `CQRSharp.FluentValidation`), which in turn resolves every
`FluentValidation.IValidator<TRequest>` in the container and runs each with `ValidateAsync`, passing the dispatch's
cancellation token through (so `MustAsync` / `WhenAsync` rules observe it).

- **No FluentValidation validator for a request** — the adapter is a no-op and reports zero failures.
  Nothing needs to be registered per request type.
- **Several validators for one request** — all of them run, in registration order, and their failures
  are combined.
- **A failure** — the behavior throws `RequestValidationException` before the handler runs, as for
  native validators. [CQRSharp.AspNetCore](aspnetcore.md#exception-mapping) maps it to a 400 validation
  ProblemDetails.
- **Streaming requests** are covered too: the stream validation behavior consumes the same
  `IRequestValidator<TRequest>` set.

Validators are matched by the **exact** request type, as the container resolves
`IValidator<TRequest>`; a validator written for a base class is not picked up for a derived request
unless you also register it as `IValidator<DerivedRequest>`.

## How failures are mapped

CQRSharp's `ValidationFailure` is `(string Code, string Message, string? MemberName)`:

| FluentValidation | CQRSharp | Notes |
| --- | --- | --- |
| `ErrorCode` | `Code` | The rule's code — your `WithErrorCode(...)`, or FluentValidation's default such as `NotEmptyValidator`. A hand-built failure with no code maps to `FluentValidationFailureMapper.UnspecifiedErrorCode` (`"FluentValidation.Unspecified"`). |
| `ErrorMessage` | `Message` | Already formatted and localized by FluentValidation. |
| `PropertyName` | `MemberName` | The full FluentValidation path (`Address.City`, `Lines[0].Sku`); `null` for a model-level failure with no property. |
| `AttemptedValue`, `CustomState`, `FormattedMessagePlaceholderValues` | — | Not carried over: `ValidationFailure` has no member for them. |

`FluentValidationFailureMapper.Map(...)` is public, so the same mapping is available if you run a
FluentValidation validator yourself.

## Severity

**Only `Severity.Error` failures fail a request.** FluentValidation marks a result invalid for
`Severity.Warning` and `Severity.Info` failures as well, but CQRSharp's `ValidationFailure` carries no
severity and any reported failure rejects the request — so the adapter drops warnings and infos rather
than turn advisory rules into hard failures.

```csharp
RuleFor(c => c.Name).MinimumLength(3).WithSeverity(Severity.Warning); // never rejects the request
RuleFor(c => c.Age).GreaterThanOrEqualTo(18);                         // Severity.Error (the default): rejects
```

Warnings and infos are not surfaced anywhere by the integration. If you need them, run the validator
yourself where you want to report them.

## Mixing with native validators

FluentValidation validators and native `IRequestValidator<TRequest>` implementations can target the
same request. Both run, and the validation behavior aggregates their failures into one
`RequestValidationException`. A native validator needs no registration: the source generator registers every
non-generic `IRequestValidator<TRequest>` it can see.

```csharp
public sealed class CreateUserNameIsFree(IUserDirectory users) : IRequestValidator<CreateUser>
{
    public async Task<ValidationFailure[]> ValidateAsync(CreateUser request, CancellationToken ct)
        => await users.ExistsAsync(request.Name, ct)
            ? [new ValidationFailure("NAME_TAKEN", "That name is taken.", nameof(request.Name))]
            : [];
}
```

Inside a file that imports both `CQRSharp` and `FluentValidation.Results`, the name `ValidationFailure`
is ambiguous. Alias the one you mean:

```csharp
using CqrsFailure = CQRSharp.ValidationFailure;
using FluentFailure = FluentValidation.Results.ValidationFailure;
```

(`using FluentValidation;` alone — all a validator class needs — does not clash.)

Do not rely on the relative order of native and FluentValidation failures in `Failures`.

## Registering without the builder

`UseFluentValidation()` calls the `IServiceCollection` extension `AddCqrsFluentValidation()`. Use it directly when you
wire things outside the builder callback:

```csharp
services.AddCqrsGenerated(b => b.ValidateOnStart());
services.AddCqrsFluentValidation();
services.AddValidatorsFromAssemblyContaining<CreateUserValidator>();
```

`AddCqrsFluentValidation()` only registers the adapter, once however often it (or `UseFluentValidation()`) is called.
It does not register the validation behavior: `AddCqrsGenerated`, in either form, does that unless
`UseValidation(false)` turns it off, and with validation off the adapter is never consulted.
Unlike the builder's own verbs, `UseFluentValidation()` applies the adapter registration to `Services` immediately.
