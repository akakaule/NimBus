using DynamicsBcDemo.Contracts.BusinessCentral;
using DynamicsBcDemo.Contracts.D365Sales;
using NimBus.Core.Endpoints;

namespace DynamicsBcDemo.Contracts.Endpoints;

/// <summary>
/// Business Central. Owns the buying customer, contacts, items and quotes. Keeps CRM's prospects and
/// opportunities so BC users can quote them, answers credit checks, and publishes the customer,
/// contact, product-group and quote changes CRM mirrors.
/// </summary>
public class BusinessCentralEndpoint : Endpoint
{
    public BusinessCentralEndpoint()
    {
        Produces<BcSalesQuoteCreated>();
        Produces<BcSalesQuoteUpdated>();
        Produces<BcCustomerCreated>();
        Produces<BcCustomerUpdated>();
        Produces<BcContactUpdated>();
        Produces<BcItemCategoryUpdated>();

        Consumes<D365ProspectUpdated>();
        Consumes<D365OpportunityUpdated>();
        Consumes<D365CreditCheckRequested>();
    }

    public override ISystem System => new BusinessCentralSystem();

    public override string Description =>
        "Business Central adapter endpoint. BC owns the buying customer, contacts and quotes: it keeps CRM's prospects and opportunities so quotes can be linked to them, answers credit checks, and publishes the customer, contact, product-group and quote changes CRM mirrors.";
}

internal sealed class BusinessCentralSystem : ISystem
{
    public string SystemId => "BusinessCentral";
}
