using DynamicsBcDemo.Contracts.BusinessCentral;
using DynamicsBcDemo.Contracts.D365Sales;
using NimBus.Core.Endpoints;

namespace DynamicsBcDemo.Contracts.Endpoints;

/// <summary>
/// Dynamics 365 Sales. Creates prospects and opportunities and owns the pipeline. Sends prospects
/// and opportunities to Business Central, where the quotes are made, asks it for credit status, and
/// mirrors the customers, contacts, product groups and quote statuses Business Central owns.
/// </summary>
public class D365SalesEndpoint : Endpoint
{
    public D365SalesEndpoint()
    {
        Produces<D365ProspectUpdated>();
        Produces<D365OpportunityUpdated>();

        // Request/reply request, answered on the auto-provisioned D365SalesEndpoint-reply
        // subscription.
        Produces<D365CreditCheckRequested>();

        Consumes<BcSalesQuoteCreated>();
        Consumes<BcSalesQuoteUpdated>();
        Consumes<BcCustomerCreated>();
        Consumes<BcCustomerUpdated>();
        Consumes<BcContactUpdated>();
        Consumes<BcItemCategoryUpdated>();
    }

    public override ISystem System => new D365SalesSystem();

    public override string Description =>
        "Dynamics 365 Sales adapter endpoint. CRM creates prospects and opportunities and sends them to Business Central, where the quotes are made; it mirrors the customers, contacts, product groups and quote statuses Business Central owns.";
}

internal sealed class D365SalesSystem : ISystem
{
    public string SystemId => "D365Sales";
}
