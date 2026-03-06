using NimBus.Core.Endpoints;
using NimBus.Events.Inventory;
using NimBus.Events.Orders;
using NimBus.Events.Payments;
using NimBus.Events.Shipping;

namespace NimBus.Endpoints.Warehouse
{
    public class WarehouseEndpoint : Endpoint
    {
        public WarehouseEndpoint()
        {
            Consumes<OrderPlaced>();
            Consumes<PaymentCaptured>();
            Produces<InventoryReserved>();
            Produces<ShipmentDispatched>();
        }

        public override ISystem System => new WarehouseSystem();

        public override string Description =>
            "Example mixed-role endpoint that reserves stock and dispatches shipments after receiving order and payment events.";
    }

    internal sealed class WarehouseSystem : ISystem
    {
        public string SystemId => "Warehouse";
    }
}
