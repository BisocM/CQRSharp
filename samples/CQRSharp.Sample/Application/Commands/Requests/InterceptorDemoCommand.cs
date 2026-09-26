using CQRSharp.Sample.Application.Contexts;
using CQRSharp.Sample.Infrastructure.Interceptors;

namespace CQRSharp.Sample.Application.Commands.Requests;

[CustomInterceptor(10)]
public sealed class InterceptorDemoCommand : CommandBase<SampleRequestContext>;
