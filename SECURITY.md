# Security Policy

## Supported versions

Security fixes are made on the latest major version only and ship as a new release of that line.

| Version | Supported |
| --- | --- |
| 5.x | Yes |
| 4.x and earlier | No — upgrade to 5.x (see the migration notes in [CHANGELOG.md](CHANGELOG.md)) |

## Reporting a vulnerability

**Do not open a public issue, discussion or pull request for a security problem.**

Report it privately through GitHub: on the repository's
[Security tab](https://github.com/BisocM/CQRSharp/security), choose **Report a vulnerability**
([direct link](https://github.com/BisocM/CQRSharp/security/advisories/new)). That opens a private advisory visible only
to you and the maintainer. If the button is not available, contact the maintainer, [@BisocM](https://github.com/BisocM),
through GitHub and ask for a private channel — without posting any details of the problem publicly.

Please include:

- the affected package(s) and version(s) (`CQRSharp.Core`, `CQRSharp.Pipelines`, `CQRSharp.Redis`,
  `CQRSharp.EntityFrameworkCore`, …);
- the .NET version, and the store and database/Redis version if one is involved;
- what an attacker can do, and what they need in order to do it (network position, ability to publish a notification,
  write access to the store, …);
- a minimal reproduction or proof of concept, and any configuration it depends on;
- whether the issue is already public or known to anyone else.

## What to expect

CQRSharp is maintained by one person in their own time, so responses are **best-effort** and no response time is
guaranteed. Reports are read, and you will get an acknowledgement and an honest assessment: whether it is accepted as a
vulnerability, and if so roughly what the fix involves. Accepted vulnerabilities are fixed in the 5.x line, released to
NuGet, and described in the changelog and a GitHub security advisory, with credit to the reporter unless you ask not to
be named.

## Disclosure

Please practise coordinated disclosure: keep the details private until a fixed version is on NuGet, or until the
maintainer and you have agreed a disclosure date. If a report goes unanswered for an extended period, a reminder on the
advisory is welcome before you consider disclosing.

## Scope

In scope: the code in this repository as published in the `CQRSharp*` NuGet packages, including the source generator's
emitted code and the build/publish workflows.

Some properties of the design are worth knowing when you assess an issue:

- **CQRSharp runs your handlers in-process, with your application's privileges.** Handlers, behaviors, interceptors,
  validators and stores that an application registers are trusted code. CQRSharp is not a sandbox and not an
  authorization layer: deciding who may dispatch which request is the application's job. "A handler can do something
  dangerous" is not a vulnerability in the library; the library dispatching a request to the wrong handler, skipping a
  configured behavior, or leaking state between requests or DI scopes is.
- **The outbox persists serialized notification payloads.** Durable notifications are written to the configured store
  (in-memory, Redis, or your EF Core database) and read back later by the registered `INotificationSerializer`. The
  default, generated serializer is reflection-free JSON over the notification types known at compile time: a stored
  message names its type by its stable `[NotificationName]`, which is looked up in that closed set, so a payload cannot
  name an arbitrary CLR type to instantiate. A serializer an application registers with
  `AddNotificationSerializer<T>()` replaces it and is responsible for the same guarantee.
  Anyone who can write to the outbox store can cause notifications to be delivered to your handlers, so the store must
  be protected like any other application database. Payloads are not encrypted by the library; do not put secrets in
  notifications unless the store itself is appropriately secured.
- **The Redis stores execute fixed Lua scripts.** The script text is constant and compiled into the package; keys and
  values are passed as script parameters (`KEYS` / `ARGV`), never concatenated into script text, and no user-supplied
  script is ever executed. A way to get caller-controlled data interpreted as script would be a vulnerability.
- **Idempotency keys and rate-limit keys come from the application.** If you derive them from untrusted input (an
  `Idempotency-Key` header, for example), bound their length and scope them per caller, as you would any cache key.

Out of scope: vulnerabilities in third-party dependencies (report those upstream; if CQRSharp's use of one makes it
exploitable, that *is* in scope), the sample applications, the benchmarks, and issues that require an already
compromised host, store or build environment.
