using NimBus.Core.Endpoints;
using NimBus.Events.Orders;

namespace NimBus.Endpoints.Sample
{
    public class AspireSampleEndpoint : Endpoint
    {
        public AspireSampleEndpoint()
        {
            Consumes<OrderPlaced>();
        }

        public override ISystem System => new AspireSampleSystem();

        public override string Description =>
            "Sample endpoint that receives OrderPlaced events routed from the StorefrontEndpoint.";
    }

    internal sealed class AspireSampleSystem : ISystem
    {
        public string SystemId => "AspireSample";
    }
}
