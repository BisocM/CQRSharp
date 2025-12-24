CQRSharp (NuGet Readme)

CQRSharp is a lightweight CQRS framework with a focus on plug-and-play configuration and NativeAOT-friendly builds.

Project links:
GitHub: https://github.com/BisocM/CQRSharp
Website: https://bisocm.org/projects/cqrsharp

Recommended installation:
Install the CQRSharp package. It brings in the runtime packages and ships the source generator analyzer so handler
registrations are generated at build time.

Packages:
CQRSharp             Meta-package (recommended). References Core, Abstractions, and Pipelines, and embeds the generator analyzer.
CQRSharp.Core        Runtime dispatcher, pipeline execution, notifications, outbox and background processing.
CQRSharp.Abstractions Shared contracts and marker interfaces used by the runtime and source generator.
CQRSharp.Pipelines   Optional pipeline behaviors plus a pipeline-pack registration entrypoint for common defaults.
CQRSharp.Generators  Source generator analyzer package (analyzer-only). Use directly only if you want the generator without the meta-package.
