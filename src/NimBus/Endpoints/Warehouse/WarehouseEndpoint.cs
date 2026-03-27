using NimBus.Core.Endpoints;
using NimBus.Events.Orders;

namespace NimBus.Endpoints.Warehouse
{
    public class WarehouseEndpoint : Endpoint
    {
        public WarehouseEndpoint()
        {
            Consumes<OrderPlaced>();
        }

        public override ISystem System => new WarehouseSystem();

        public override string Description =>
            "Subscriber endpoint that processes OrderPlaced events for inventory and shipping.";
    }

    internal sealed class WarehouseSystem : ISystem
    {
        public string SystemId => "Warehouse";
    }
}
