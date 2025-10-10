using CQRSharp.Abstractions.Data.Attributes.Pipelines;
using CQRSharp.Abstractions.Data.Interfaces.Markers.Command;
using CQRSharp.Abstractions.Data.Models.Commands;
using CQRSharp.Sample.Application.Contexts;
using CQRSharp.Sample.Application.Pipelines;

namespace CQRSharp.Sample.Application.Commands.Requests;

[PipelineExemption(typeof(LoggingPipelineBehavior<PingCommand, CommandResult>))]
public class PingCommand : CommandBase<SampleRequestContext>;