using CQRSharp.Abstractions.Interfaces.Markers.Command;
using CQRSharp.Abstractions.Interfaces.Markers.Request;
using CQRSharp.Sample.Application.Contexts;

namespace CQRSharp.Sample.Application.Commands.Requests;

// Opts into resilience retries: the handler deliberately fails the first attempts, then succeeds, to exercise the
// retry behavior. Retries are now opt-in via IRetryableRequest (most commands are not idempotent and must not retry).
public class FailingCommand : CommandBase<SampleRequestContext>, IRetryableRequest;