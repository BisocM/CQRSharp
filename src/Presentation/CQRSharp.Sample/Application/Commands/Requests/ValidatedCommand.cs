using CQRSharp.Abstractions.Data.Interfaces.Markers.Command;
using CQRSharp.Sample.Application.Contexts;

namespace CQRSharp.Sample.Application.Commands.Requests;

public sealed class ValidatedCommand(string? value) : CommandBase<SampleRequestContext>
{
    public string? Value { get; } = value;
}

