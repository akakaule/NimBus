using NimBus.Core.Endpoints;
using NimBus.Endpoints.CRM;
using NimBus.Events.Lead;
using NimBus.Events.WebInquiry;
using System;
using System.Collections.Generic;
using System.Text;

namespace NimBus.Endpoints.Website
{
    public class WebEndpoint : Endpoint
    {
        public WebEndpoint()
        {
            Produces<LeadCreated>();
            Produces<LeadUpdated>();
            Produces<WebInquiryCreated>();
        }

        public override ISystem System => new WebSystem();
        public override string Description => "Publishes Website events. Consumes events from the website if a lead is created or updated.";
    }
}
