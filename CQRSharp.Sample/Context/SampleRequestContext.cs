using CQRSharp.Interfaces.Context;
using CQRSharp.Sample.Management.Menu;

namespace CQRSharp.Sample.Context
{
    public class SampleRequestContext(string requestId, string userId)
        : RequestContextBase(requestId, userId)
    {
        /// <summary>
        /// The desired menu state after the completion of the command. Remains null if no menu state needed.
        /// </summary>
        public MenuState? NextState { get; set; }
    }
}