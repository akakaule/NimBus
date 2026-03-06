using NimBus.Core.Endpoints;
using NimBus.Events.Customers;
using NimBus.Events.Inventory;
using NimBus.Events.Notifications;
using NimBus.Events.Orders;
using NimBus.Events.Payments;
using NimBus.Events.Shipping;

namespace NimBus.Endpoints.Analytics
{
    public class AnalyticsEndpoint : Endpoint
    {
        public AnalyticsEndpoint()
        {
            Consumes<CustomerRegistered>();
            Consumes<OrderPlaced>();
            Consumes<PaymentCaptured>();
            Consumes<InventoryReserved>();
            Consumes<ShipmentDispatched>();
            Consumes<CustomerNotified>();
        }

        public override ISystem System => new AnalyticsSystem();

        public override string Description =>
            "Consume-only example endpoint that subscribes to all demo events for reporting and dashboards.";
    }

    internal sealed class AnalyticsSystem : ISystem
    {
        public string SystemId => "Analytics";
    }
}
