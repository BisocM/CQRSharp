using CQRSharp.Core.Pipelines.Attributes.Markers;
using CQRSharp.Interfaces.Markers.Command;
using CQRSharp.Sample.Attributes;

namespace CQRSharp.Sample.Commands;

[CustomInterceptor(2)]
public class CreateUserCommand : CommandBase
{
    public string UserName { get; set; }

    [SensitiveData] public string Password { get; set; }

    // The handler sets this after user creation
    public Guid CreatedUserId { get; set; }
}