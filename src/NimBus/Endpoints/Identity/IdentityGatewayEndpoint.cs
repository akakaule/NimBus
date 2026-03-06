using NimBus.Core.Endpoints;
using NimBus.Events.Customers;

namespace NimBus.Endpoints.Identity
{
    public class IdentityGatewayEndpoint : Endpoint
    {
        public IdentityGatewayEndpoint()
        {
            Produces<CustomerRegistered>();
        }

        public override ISystem System => new IdentityGatewaySystem();

        public override string Description =>
            "Publish-only example endpoint that emits customer registration events when new users join the platform.";
    }

    internal sealed class IdentityGatewaySystem : ISystem
    {
        public string SystemId => "IdentityGateway";
    }
}
