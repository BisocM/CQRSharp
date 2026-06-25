using CQRSharp.Abstractions.Interfaces.Markers.Command;
using CQRSharp.Sample.Application.Contexts;

namespace CQRSharp.Sample.Application.Commands.Requests;

public sealed class ExceptionDemoCommand : CommandBase<SampleRequestContext>;