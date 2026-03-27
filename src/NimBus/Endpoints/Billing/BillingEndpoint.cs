using NimBus.Core.Endpoints;
using NimBus.Events.Orders;

namespace NimBus.Endpoints.Billing
{
    public class BillingEndpoint : Endpoint
    {
        public BillingEndpoint()
        {
            Consumes<OrderPlaced>();
        }

        public override ISystem System => new BillingSystem();

        public override string Description =>
            "Subscriber endpoint that processes OrderPlaced events for payment handling.";
    }

    internal sealed class BillingSystem : ISystem
    {
        public string SystemId => "Billing";
    }
}
