using CQRSharp.Sample.Application.Contexts;
using CQRSharp.Sample.Application.Pipelines;

namespace CQRSharp.Sample.Application.Commands.Requests;

// The open-generic form exempts the behavior for every request it is closed over (here, just PingCommand) and is the
// idiomatic shorthand for the verbose typeof(RequestCountingBehavior<PingCommand, CommandResult>) - see CQRA008.
[PipelineExemption(typeof(RequestCountingBehavior<,>))]
public sealed class PingCommand : CommandBase<SampleRequestContext>;
