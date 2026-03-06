using NimBus.Core.Endpoints;
using NimBus.Events.Contacts;
using NimBus.Events.Customers;
using System;
using System.Collections.Generic;
using System.Text;

namespace NimBus.Endpoints.CRM
{
    public class CrmBulkEndpoint : Endpoint
    {
        public CrmBulkEndpoint()
        {
            Consumes<ContactCreatedBulk>();
            Consumes<AccountCreatedBulk>();
        }

        public override ISystem System => new CrmSystem();
        public override string Description => "Consumes events by calling the CRM Web API. Runs in an Azure Functions.";
    }
}
