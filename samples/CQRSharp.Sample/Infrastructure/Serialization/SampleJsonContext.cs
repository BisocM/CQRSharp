using System.Text.Json.Serialization;
using CQRSharp.Sample.Domain.Entities;

namespace CQRSharp.Sample.Infrastructure.Serialization;

/// <summary>
///     The result types the idempotency behavior stores and replays (Program.cs passes this context to
///     <c>ReplayResultsWith</c>). Source-generated, so replay needs no reflection and works under Native AOT; a result type
///     missing here is not replayed, and its duplicate is rejected instead.
/// </summary>
[JsonSerializable(typeof(CommandResult<Receipt>))]
[JsonSerializable(typeof(decimal))]
internal sealed partial class SampleJsonContext : JsonSerializerContext;
