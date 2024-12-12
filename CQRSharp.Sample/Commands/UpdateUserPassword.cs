using CQRSharp.Core.Pipelines.Attributes.Markers;
using CQRSharp.Interfaces.Markers.Command;
using CQRSharp.Sample.Context;

namespace CQRSharp.Sample.Commands;

public class UpdateUserPasswordCommand : CommandBase
{
    public Guid UserId { get; set; }

    [SensitiveData] public string NewPassword { get; set; }
}