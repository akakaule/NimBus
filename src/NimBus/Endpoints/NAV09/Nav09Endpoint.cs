using NimBus.Core.Endpoints;
using NimBus.Events.Brand;
using NimBus.Events.Contacts;
using NimBus.Events.Currencies;
using NimBus.Events.Customers;

namespace NimBus.Endpoints.NAV09
{
    public class Nav09Endpoint : Endpoint
    {
        public Nav09Endpoint()
        {
            Produces<ProspectUpdated>();
            Produces<ProspectDeactivated>();
            Produces<VendorCreated>();
            Produces<ContactUpdatedNav>();
            Produces<ContactCreatedNav>();
            Produces<ContactDeactivatedNav>();

            Produces<AccountCreatedBulk>();
            Produces<ContactCreatedBulk>();

            Produces<CurrencyCreated>();
            Produces<CurrencyUpdated>();
            Produces<CurrencyDeactivated>();

            Produces<BrandCreated>();
            Produces<BrandUpdated>();
            Produces<BrandDeactivated>();

            Consumes<AccountCreated>();
            Consumes<AccountUpdated>();
            Consumes<AccountDeactivated>();
            Consumes<ContactCreated>();
            Consumes<ContactUpdatedCRM>();
            Consumes<ContactDeactivated>();
        }        

        public override ISystem System => new Nav09System();
        public override string Description => "Publishes Nav09 events. Runs from Nav09 Plugins and Azure Functions. Consumes events by calling the Nav09 Web API. Runs in an Azure Functions.";
    }
}
