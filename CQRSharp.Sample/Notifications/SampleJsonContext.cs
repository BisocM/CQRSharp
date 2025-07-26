using System.Text.Json.Serialization;
using CQRSharp.Sample.Commands.Types;
using CQRSharp.Sample.Queries.Types;

namespace CQRSharp.Sample.Notifications;

[JsonSerializable(typeof(UserCreatedNotification))]
[JsonSerializable(typeof(CreateUserCommand))]
[JsonSerializable(typeof(FailingCommand))]
[JsonSerializable(typeof(InterceptorDemoCommand))]
[JsonSerializable(typeof(PingCommand))]
[JsonSerializable(typeof(SlowCommand))]
[JsonSerializable(typeof(UpdateUserPasswordCommand))]
[JsonSerializable(typeof(GetUserQuery))]
// Also add this for the sanitization logic
[JsonSerializable(typeof(Dictionary<string, System.Text.Json.JsonElement>))] 
public partial class SampleJsonContext : JsonSerializerContext
{
}