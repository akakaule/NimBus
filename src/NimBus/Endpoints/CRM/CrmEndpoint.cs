using NimBus.Core.Endpoints;
using NimBus.Events.Brand;
using NimBus.Events.Contacts;
using NimBus.Events.Currencies;
using NimBus.Events.Customers;
using NimBus.Events.Lead;
using NimBus.Events.SurveyMonkey;
using NimBus.Events.WebInquiry;

namespace NimBus.Endpoints.CRM
{
    public class CrmEndpoint : Endpoint
    {
        public CrmEndpoint()
        {
            Produces<AccountCreated>();
            Produces<AccountUpdated>();
            Produces<AccountDeactivated>();
            Produces<ContactCreated>();
            Produces<ContactUpdatedCRM>();
            Produces<ContactDeactivated>();

            Consumes<ProspectUpdated>();
            Consumes<ProspectDeactivated>();
            Consumes<VendorCreated>();
            Consumes<ContactUpdatedNav>();
            Consumes<ContactDeactivatedNav>();
            Consumes<ContactCreatedNav>();
            Consumes<SurveyCreated>();
            Consumes<SurveyUpdated>();
            Consumes<LeadCreated>();
            Consumes<LeadUpdated>();
            Consumes<CurrencyCreated>();
            Consumes<CurrencyUpdated>();
            Consumes<CurrencyDeactivated>();
            Consumes<BrandCreated>();
            Consumes<BrandUpdated>();
            Consumes<BrandDeactivated>();
            Consumes<WebInquiryCreated>();
        }

        public override ISystem System => new CrmSystem();
        public override string Description => "Publishes CRM events. Runs from CRM Plugins and Azure Functions. Consumes events by calling the CRM Web API. Runs in an Azure Functions.";
    }
}
