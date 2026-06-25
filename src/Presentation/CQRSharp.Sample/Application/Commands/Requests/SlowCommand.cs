using CQRSharp.Abstractions.Interfaces.Markers.Command;
using CQRSharp.Sample.Application.Contexts;

namespace CQRSharp.Sample.Application.Commands.Requests;

public class SlowCommand : CommandBase<SampleRequestContext>;