# Roslyn Source Generator Pipeline Review (v2.2.7)

This note summarizes observations on the source generators in `CQRSharp.Generators` and highlights potential efficiency improvements.

## Overview

The project exposes multiple incremental generators:

- `HandlerRegistrationGenerator`
- `HandlerRegistryGenerator`
- `PipelineRegistryGenerator`
- `RequestRegistryGenerator`
- `ContextFactoryRegistryGenerator`

Each generator currently registers a syntax provider that selects **all** `ClassDeclarationSyntax` (and in some cases record declarations) and then collects every candidate symbol before generating a single output file.

## Observed Inefficiencies

1. **Broad syntax filters** – Most generators use `node is ClassDeclarationSyntax` as the predicate when creating the `SyntaxProvider`. This means Roslyn analyzes every class in the compilation even though only a subset is relevant.
2. **Collecting all symbols** – The pipeline calls `.Collect()` on the provider and then processes the entire array in one step. A single change forces Roslyn to re‑evaluate every candidate symbol and regenerate the full output.
3. **Late semantic analysis** – Semantic model lookups (`GetDeclaredSymbol`) occur inside `RegisterSourceOutput`. Performing these lookups earlier in the pipeline would avoid re‑computing them during regeneration.

## Potential Improvements

- **Filter earlier** – Use `SyntaxProvider.ForAttributeWithMetadataName` or predicate logic that checks for known attributes/interfaces at the syntax level (e.g., presence of `[HandlerType]`). This drastically reduces the amount of syntax nodes analyzed.
- **Incremental transformations** – Instead of collecting all candidates at once, transform each candidate into a lightweight DTO (containing the necessary metadata) inside the provider, then aggregate those DTOs. Changes to unrelated files would no longer invalidate the entire registry.
- **Pre‑compute semantic information** – Move `GetDeclaredSymbol` calls into the provider’s transform function so Roslyn caches the semantic model results incrementally.
- **Avoid redundant `Distinct()` calls** – Several generators call `.Distinct()` on lists that were already deduplicated. Maintaining a `HashSet` during collection avoids extra allocations.

Applying these adjustments would make the generators respond faster to edits and reduce memory usage during large builds.

## Runtime Pipeline Implementation

Earlier versions populated several `ConcurrentDictionary` instances at application startup to store request metadata, handler delegates and pipeline builders. These mappings are now plain `Dictionary` objects because they are built once and never mutated.

### Observed Inefficiencies

- **Unnecessary concurrent collections** – The registries now use regular `Dictionary` objects since entries are added only once during initialization. This avoids locking overhead and reduces memory usage.
- **Per-request pipeline construction** – `RequestDispatcher.BuildPipeline` invokes the generated pipeline builder each time a request is executed. The builder allocates arrays of pipeline behaviors and composes delegates on every call. Caching the composed pipeline for each request type would eliminate these allocations.
- **ContinueWith allocations** – Pipeline builders use `ContinueWith` to convert results to `object`. An `async` method or direct cast would avoid creating additional tasks.
- **Repeated DI lookups** – Every pipeline execution resolves behaviors and factories from `IServiceProvider`. If behaviors are stateless, caching them or using singleton registrations would reduce repeated service resolution overhead.

By addressing these areas, the runtime portion of the pipeline can execute with fewer allocations and less locking, complementing the improvements suggested for the generators above.

## Updates in v2.2.7

The generated pipeline builders now compose a chain of `Func<CancellationToken, Task<TResult>>` delegates instead of passing the request instance between behaviors. Each behavior is invoked directly and the final handler is awaited using `async` rather than `ContinueWith`. This removes per-call array allocations and reduces delegate chaining overhead during request execution.
