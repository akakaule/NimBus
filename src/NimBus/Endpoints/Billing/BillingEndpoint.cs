using NimBus.Core.Endpoints;
using NimBus.Events.Orders;
using NimBus.Events.Payments;

namespace NimBus.Endpoints.Billing
{
    public class BillingEndpoint : Endpoint
    {
        public BillingEndpoint()
        {
            Consumes<OrderPlaced>();
            Produces<PaymentCaptured>();
        }

        public override ISystem System => new BillingSystem();

        public override string Description =>
            "Example mixed-role endpoint that subscribes to order events and publishes payment confirmation events.";
    }

    internal sealed class BillingSystem : ISystem
    {
        public string SystemId => "Billing";
    }
}
