using NimBus.Core.Endpoints;
using NimBus.Events.Customers;
using NimBus.Events.Orders;

namespace NimBus.Endpoints.Billing
{
    public class BillingEndpoint : Endpoint
    {
        public BillingEndpoint()
        {
            Consumes<OrderPlaced>();
            Consumes<OrderDeliveryDetailsCaptured>();

            // Sole consumer of this command — PlatformValidation.ValidateCommandConsumers
            // fails provisioning if a second endpoint declares it consumed.
            Consumes<PlaceCustomerOnCreditHold>();
        }

        public override ISystem System => new BillingSystem();

        public override string Description =>
            "Subscriber endpoint that processes order events for payment handling and acts on credit-hold commands.";
    }

    internal sealed class BillingSystem : ISystem
    {
        public string SystemId => "Billing";
    }
}
