using CQRSharp.Interfaces.Handlers;
using CQRSharp.Interfaces.Markers;

namespace CQRSharpSample.Commands
{
    /// <summary>
    /// Command to calculate the sum of two integers.
    /// </summary>
    public class CalculateSumCommand : ICommand<int>
    {
        public required int Value1 { get; set; }
        public required int Value2 { get; set; }
    }

    /// <summary>
    /// Handler to calculate the sum of two integers.
    /// </summary>
    public class CalculateSumCommandHandler : ICommandHandler<CalculateSumCommand, int>
    {
        public Task<int> Handle(CalculateSumCommand command, CancellationToken cancellationToken)
        {
            int result = command.Value1 + command.Value2;
            return Task.FromResult(result);
        }
    }
}
