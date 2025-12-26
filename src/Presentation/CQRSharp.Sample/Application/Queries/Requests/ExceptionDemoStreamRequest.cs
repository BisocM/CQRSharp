using CQRSharp.Abstractions.Data.Interfaces.Markers.Stream;
using CQRSharp.Sample.Application.Contexts;

namespace CQRSharp.Sample.Application.Queries.Requests;

public sealed class ExceptionDemoStreamRequest : StreamRequestBase<ExceptionDemoStreamItem, SampleRequestContext>;

public sealed record ExceptionDemoStreamItem(int Value);
