using NimBus.Core.Endpoints;
using NimBus.Events.Orders;

namespace NimBus.Endpoints.Storefront
{
    public class StorefrontEndpoint : Endpoint
    {
        public StorefrontEndpoint()
        {
            Produces<OrderPlaced>();
        }

        public override ISystem System => new StorefrontSystem();

        public override string Description =>
            "Publisher endpoint that produces OrderPlaced events when customers place orders.";
    }

    internal sealed class StorefrontSystem : ISystem
    {
        public string SystemId => "Storefront";
    }
}
