using CrmErpDemo.Contracts.Events;
using NimBus.Core.Endpoints;

namespace CrmErpDemo.Contracts.Endpoints;

public class ErpEndpoint : Endpoint
{
    public ErpEndpoint()
    {
        Produces<ErpCustomerCreated>();
        Produces<ErpCustomerUpdated>();
        Produces<ErpCustomerDeleted>();
        Produces<ErpContactCreated>();
        Produces<ErpContactUpdated>();
        Produces<ErpContactDeleted>();

        Consumes<CrmAccountCreated>();
        Consumes<CrmAccountUpdated>();
        Consumes<CrmAccountDeleted>();
        Consumes<CrmContactCreated>();
        Consumes<CrmContactUpdated>();
        Consumes<CrmContactDeleted>();
    }

    public override ISystem System => new ErpSystem();

    public override string Description =>
        "ERP adapter endpoint. Consumes Crm-prefixed Account/Contact events and acknowledges with ErpCustomerCreated.";
}

internal sealed class ErpSystem : ISystem
{
    public string SystemId => "Erp";
}
