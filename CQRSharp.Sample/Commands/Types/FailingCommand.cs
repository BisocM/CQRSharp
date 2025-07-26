using CQRSharp.Abstractions.Data.Interfaces.Markers.Command;
using CQRSharp.Sample.Context;

namespace CQRSharp.Sample.Commands.Types;

public class FailingCommand : CommandBase<SampleRequestContext>;