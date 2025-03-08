using System;
using CQRSharp.Core.Factories;
using CQRSharp.Interfaces.Context;
using CQRSharp.Interfaces.Markers.Request;
using CQRSharp.Sample.Context;

namespace CQRSharp.Sample.Factories
{
    //This class is REQUIRED for the automatic population of all the contexts for all commands.
    public class CustomRequestContextFactory(IServiceProvider serviceProvider) : IRequestContextFactory
    {
        public IRequestContext CreateContext(IRequest request)
        {
            //Provide fallback if factories aren't registered
            var requestId = Guid.NewGuid().ToString();

            var userId = "STATIC_USER";

            return new SampleRequestContext(requestId, userId);
        }
    }
}