using System.Text.Json;
using System.Text.Json.Serialization;
using CQRSharp.Sample.AspNetCore.Orders;

namespace CQRSharp.Sample.AspNetCore;

/// <summary>
///     Every type the API reads or writes as JSON, and every result the idempotency behavior replays. Source-generated, so
///     neither the endpoints nor the replay need reflection, and both work under Native AOT. The web defaults (camelCase,
///     case-insensitive names) are the ones ASP.NET Core writes with, so a client reading through this context reads what
///     the API wrote.
/// </summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(PlaceOrderBody))]
[JsonSerializable(typeof(OrderDto))]
[JsonSerializable(typeof(Guid))]
[JsonSerializable(typeof(CommandResult<Guid>))]
internal sealed partial class SampleJsonContext : JsonSerializerContext;
