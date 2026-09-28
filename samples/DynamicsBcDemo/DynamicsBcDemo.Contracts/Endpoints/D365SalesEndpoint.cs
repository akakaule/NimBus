using DynamicsBcDemo.Contracts.BusinessCentral;
using DynamicsBcDemo.Contracts.D365Sales;
using NimBus.Core.Endpoints;

namespace DynamicsBcDemo.Contracts.Endpoints;

/// <summary>
/// Dynamics 365 Sales. Owns leads, opportunities and the pipeline — everything until a prospect
/// becomes a buying customer. Asks Business Central for quotes and credit status, and mirrors the
/// customer, quote and order data that Business Central owns.
/// </summary>
public class D365SalesEndpoint : Endpoint
{
    public D365SalesEndpoint()
    {
        // Command with exactly one consumer (BusinessCentralEndpoint); platform validation fails
        // provisioning if a second endpoint ever declares it consumed.
        Produces<CreateBcSalesQuote>();
        Produces<D365ProspectUpdated>();

        // Request/reply request, answered on the auto-provisioned D365SalesEndpoint-reply
        // subscription.
        Produces<D365CreditCheckRequested>();

        Consumes<BcSalesQuoteCreated>();
        Consumes<BcSalesQuoteUpdated>();
        Consumes<BcCustomerCreated>();
        Consumes<BcCustomerUpdated>();
        Consumes<BcSalesOrderCreated>();
    }

    public override ISystem System => new D365SalesSystem();

    public override string Description =>
        "Dynamics 365 Sales adapter endpoint. CRM owns leads, opportunities and the pipeline; it sends quote requests to Business Central and mirrors the customers, quotes and orders Business Central owns.";
}

internal sealed class D365SalesSystem : ISystem
{
    public string SystemId => "D365Sales";
}
