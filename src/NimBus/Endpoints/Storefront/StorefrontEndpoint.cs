using NimBus.Core.Endpoints;
using NimBus.Events.Customers;
using NimBus.Events.Orders;

namespace NimBus.Endpoints.Storefront
{
    public class StorefrontEndpoint : Endpoint
    {
        public StorefrontEndpoint()
        {
            Produces<OrderPlaced>();
            Produces<OrderDeliveryDetailsCaptured>();
            Produces<PlaceCustomerOnCreditHold>();
        }

        public override ISystem System => new StorefrontSystem();

        public override string Description =>
            "Publisher endpoint that produces order events when customers place orders, supply delivery details, or trigger a credit review.";
    }

    internal sealed class StorefrontSystem : ISystem
    {
        public string SystemId => "Storefront";
    }
}
