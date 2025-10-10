using CQRSharp.Abstractions.Data.Attributes.Pipelines;
using CQRSharp.Abstractions.Data.Interfaces.Markers.Command;
using CQRSharp.Sample.Application.Contexts;

namespace CQRSharp.Sample.Application.Commands.Requests;

public class UpdateUserPasswordCommand(Guid userId, string newPassword) : CommandBase<SampleRequestContext>
{
    public Guid UserId { get; } = userId;

    [SensitiveData] public string NewPassword { get; } = newPassword;
}