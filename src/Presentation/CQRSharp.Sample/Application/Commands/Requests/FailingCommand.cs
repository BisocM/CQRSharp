using CQRSharp.Abstractions.Data.Interfaces.Markers.Command;
using CQRSharp.Sample.Application.Contexts;

namespace CQRSharp.Sample.Application.Commands.Requests;

public class FailingCommand : CommandBase<SampleRequestContext>;