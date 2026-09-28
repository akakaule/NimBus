using DynamicsBcDemo.Contracts.BusinessCentral;
using DynamicsBcDemo.Contracts.D365Sales;
using NimBus.Core.Endpoints;

namespace DynamicsBcDemo.Contracts.Endpoints;

/// <summary>
/// Business Central. Owns the buying customer, sales quotes and sales orders. Creates quotes on
/// request from CRM, answers credit checks, and publishes every change CRM mirrors.
/// </summary>
public class BusinessCentralEndpoint : Endpoint
{
    public BusinessCentralEndpoint()
    {
        Produces<BcSalesQuoteCreated>();
        Produces<BcSalesQuoteUpdated>();
        Produces<BcCustomerCreated>();
        Produces<BcCustomerUpdated>();
        Produces<BcSalesOrderCreated>();

        // BusinessCentralEndpoint must stay the command's ONLY consumer.
        Consumes<CreateBcSalesQuote>();
        Consumes<D365ProspectUpdated>();
        Consumes<D365CreditCheckRequested>();
    }

    public override ISystem System => new BusinessCentralSystem();

    public override string Description =>
        "Business Central adapter endpoint. BC owns the buying customer, quotes and orders: it creates quotes for CRM opportunities, answers credit checks, and publishes the customer, quote and order changes CRM mirrors.";
}

internal sealed class BusinessCentralSystem : ISystem
{
    public string SystemId => "BusinessCentral";
}
