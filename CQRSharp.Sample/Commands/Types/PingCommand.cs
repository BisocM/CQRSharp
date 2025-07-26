using CQRSharp.Abstractions.Data.Attributes.Pipelines;
using CQRSharp.Abstractions.Data.Interfaces.Markers.Command;
using CQRSharp.Sample.Context;
using CQRSharp.Sample.Pipelines;

namespace CQRSharp.Sample.Commands.Types;

[PipelineExemption(typeof(LoggingPipelineBehavior<PingCommand, CQRSharp.Abstractions.Data.Models.Commands.CommandResult>))]
public class PingCommand : CommandBase<SampleRequestContext>;