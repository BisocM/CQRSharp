using CQRSharp.Core.Pipelines.Attributes.Markers;
using CQRSharp.Interfaces.Markers.Command;
using CQRSharp.Sample.Context;

namespace CQRSharp.Sample.Commands;

public class CreateUserCommand : CommandBase
{
    public string UserName { get; set; }

    [SensitiveData] public string Password { get; set; }

    // The handler sets this after user creation
    public Guid CreatedUserId { get; set; }
}