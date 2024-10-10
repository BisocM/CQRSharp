using CQRSharp.Interfaces.Handlers;
using CQRSharp.Interfaces.Markers.Query;
using CQRSharpSample.Attributes;

namespace CQRSharpSample.Commands
{
    /// <summary>
    /// Command to calculate the sum of two integers.
    /// </summary>
    [Log]
    public class CalculateSumQuery : QueryBase<int>
    {
        public required int Value1 { get; set; }
        public required int Value2 { get; set; }
    }

    /// <summary>
    /// Handler to calculate the sum of two integers.
    /// </summary>
    public class CalculateSumQueryHandler : IQueryHandler<CalculateSumQuery, int>
    {
        public Task<int> Handle(CalculateSumQuery command, CancellationToken cancellationToken)
        {
            int result = command.Value1 + command.Value2;
            return Task.FromResult(result);
        }
    }
}
