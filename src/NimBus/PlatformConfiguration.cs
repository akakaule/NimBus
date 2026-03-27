using NimBus.Core;
using NimBus.Endpoints.Billing;
using NimBus.Endpoints.Storefront;
using NimBus.Endpoints.Warehouse;

namespace NimBus
{
    public class PlatformConfiguration : Platform
    {
        public PlatformConfiguration()
        {
            AddEndpoint(new StorefrontEndpoint());
            AddEndpoint(new BillingEndpoint());
            AddEndpoint(new WarehouseEndpoint());
        }
    }
}
