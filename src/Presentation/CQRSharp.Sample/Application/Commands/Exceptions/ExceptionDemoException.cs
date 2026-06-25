namespace CQRSharp.Sample.Application.Commands.Exceptions;

public sealed class ExceptionDemoException : Exception
{
    public ExceptionDemoException()
        : base("Exception demo.")
    {
    }
}