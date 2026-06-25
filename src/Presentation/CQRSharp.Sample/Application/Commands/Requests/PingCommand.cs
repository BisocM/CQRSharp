using CQRSharp.Abstractions.Attributes.Pipelines;
using CQRSharp.Abstractions.Interfaces.Markers.Command;
using CQRSharp.Abstractions.Models.Commands;
using CQRSharp.Sample.Application.Contexts;
using CQRSharp.Sample.Application.Pipelines;

namespace CQRSharp.Sample.Application.Commands.Requests;

[PipelineExemption(typeof(LoggingPipelineBehavior<PingCommand, CommandResult>))]
public class PingCommand : CommandBase<SampleRequestContext>;