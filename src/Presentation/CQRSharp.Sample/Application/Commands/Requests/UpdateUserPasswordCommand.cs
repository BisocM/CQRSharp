using CQRSharp.Abstractions.Interfaces.Markers.Command;
using CQRSharp.Sample.Application.Contexts;

namespace CQRSharp.Sample.Application.Commands.Requests;

public class UpdateUserPasswordCommand(Guid userId, string newPassword) : CommandBase<SampleRequestContext>
{
    public Guid UserId { get; } = userId;

    public string NewPassword { get; } = newPassword;
}
