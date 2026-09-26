namespace CQRSharp.Sample.Application.Queries.Exceptions;

public sealed class ExceptionDemoStreamException : Exception
{
    public ExceptionDemoStreamException()
        : base("Exception stream demo.")
    {
    }
}
