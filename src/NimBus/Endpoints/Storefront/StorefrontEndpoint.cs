using NimBus.Core.Endpoints;
using NimBus.Events.Notifications;
using NimBus.Events.Orders;
using NimBus.Events.Shipping;

namespace NimBus.Endpoints.Storefront
{
    public class StorefrontEndpoint : Endpoint
    {
        public StorefrontEndpoint()
        {
            Produces<OrderPlaced>();
            Consumes<ShipmentDispatched>();
            Consumes<CustomerNotified>();
        }

        public override ISystem System => new StorefrontSystem();

        public override string Description =>
            "Example mixed-role endpoint that publishes orders and reacts to downstream shipment and notification updates.";
    }

    internal sealed class StorefrontSystem : ISystem
    {
        public string SystemId => "Storefront";
    }
}
