using CQRSharp.Core.Pipeline;

namespace CQRSharpSample.Attributes
{
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
    public class LogEntryAttribute : Attribute, IPreCommandAttribute
    {
        public Task OnBeforeHandle(object command, IServiceProvider serviceProvider, CancellationToken cancellationToken)
        {
            Console.WriteLine($"[Attribute] Logging command ON ENTRY {command.GetType().Name}");
            return Task.CompletedTask;
        }
    }
}