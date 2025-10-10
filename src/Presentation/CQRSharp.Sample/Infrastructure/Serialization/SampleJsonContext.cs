using System.Text.Json;
using System.Text.Json.Serialization;
using CQRSharp.Sample.Application.Commands.Requests;
using CQRSharp.Sample.Application.Queries.Requests;
using CQRSharp.Sample.Domain.Events;

namespace CQRSharp.Sample.Infrastructure.Serialization;

[JsonSerializable(typeof(UserCreatedNotification))]
[JsonSerializable(typeof(CreateUserCommand))]
[JsonSerializable(typeof(FailingCommand))]
[JsonSerializable(typeof(InterceptorDemoCommand))]
[JsonSerializable(typeof(PingCommand))]
[JsonSerializable(typeof(SlowCommand))]
[JsonSerializable(typeof(UpdateUserPasswordCommand))]
[JsonSerializable(typeof(GetUserQuery))]

// Also add this for the sanitization logic
[JsonSerializable(typeof(Dictionary<string, JsonElement>))]
public partial class SampleJsonContext : JsonSerializerContext
{
}