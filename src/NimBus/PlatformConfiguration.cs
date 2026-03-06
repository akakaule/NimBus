using NimBus.Core;
using NimBus.Endpoints.Analytics;
using NimBus.Endpoints.Billing;
using NimBus.Endpoints.Identity;
using NimBus.Endpoints.Notifications;
using NimBus.Endpoints.Storefront;
using NimBus.Endpoints.Warehouse;

namespace NimBus
{
    public class PlatformConfiguration : Platform
    {
        public PlatformConfiguration()
        {
            AddEndpoint(new IdentityGatewayEndpoint());
            AddEndpoint(new StorefrontEndpoint());
            AddEndpoint(new BillingEndpoint());
            AddEndpoint(new WarehouseEndpoint());
            AddEndpoint(new NotificationEndpoint());
            AddEndpoint(new AnalyticsEndpoint());
        }
    }
}
