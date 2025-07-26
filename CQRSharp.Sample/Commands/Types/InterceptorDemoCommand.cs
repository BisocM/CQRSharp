using CQRSharp.Abstractions.Data.Interfaces.Markers.Command;
using CQRSharp.Sample.Attributes;
using CQRSharp.Sample.Context;

namespace CQRSharp.Sample.Commands.Types;

[CustomInterceptor(10)]
public class InterceptorDemoCommand : CommandBase<SampleRequestContext>;