using CrmErpDemo.Contracts.Events;
using NimBus.Core.Endpoints;

namespace CrmErpDemo.Contracts.Endpoints;

public class CrmEndpoint : Endpoint
{
    public CrmEndpoint()
    {
        Produces<CrmAccountCreated>();
        Produces<CrmAccountUpdated>();
        Produces<CrmAccountDeleted>();
        Produces<CrmContactCreated>();
        Produces<CrmContactUpdated>();
        Produces<CrmContactDeleted>();

        Consumes<ErpCustomerCreated>();
        Consumes<ErpCustomerUpdated>();
        Consumes<ErpCustomerDeleted>();
        Consumes<ErpContactCreated>();
        Consumes<ErpContactUpdated>();
        Consumes<ErpContactDeleted>();
    }

    public override ISystem System => new CrmSystem();

    public override string Description =>
        "CRM adapter endpoint. Publishes Crm-prefixed Account/Contact events; consumes Erp-prefixed acknowledgments and counter-updates.";
}

internal sealed class CrmSystem : ISystem
{
    public string SystemId => "Crm";
}
