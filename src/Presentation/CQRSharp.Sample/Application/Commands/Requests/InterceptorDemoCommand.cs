using CQRSharp.Abstractions.Interfaces.Markers.Command;
using CQRSharp.Sample.Application.Contexts;
using CQRSharp.Sample.Infrastructure.Interceptors;

namespace CQRSharp.Sample.Application.Commands.Requests;

[CustomInterceptor(10)]
public class InterceptorDemoCommand : CommandBase<SampleRequestContext>;