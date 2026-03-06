using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel;
using System.Text;
using NimBus.Core.Events;

namespace NimBus.Events.Lead
{
    public class LeadUpdated : Event
    {
        public static LeadUpdated Example = new LeadUpdated()
        {
            LeadId = Guid.NewGuid(),
            Name = "Lars Larsen",
            Address = "Road 1",
            Zip = "1234",
            City = "City",
            CountryCode = "DK",
            ContactName = "Lars Larsen",
            ContactEmail = "Lars@Larsen.eet.dk",
            Homepage = "lars.dk",
            CvrNumber = "1234567890",
            CompanyRegistrationNumber = "1234567890",
            Phone = "12345678",
            EmailInvoice = "lars.invoice@email.eet.dk",
            Newsletter = false,
            Origin = "Text",
            Duns = "123456",
            EmailStatement = "lars.statement@email.eet.dk",
            EmailCompany = "lars.company@email.eet.dk",
            ContactPerson = "Lars Larsen",
            CampaignSource = "Building",
            BusinessEntityId = 10,
            BusinessLines = "line1, line2, line3"
        };

        [Required]
        [Description("The CRM account record unique identifier")]
        public Guid LeadId { get; set; }

        [Description("The company name")]
        public string Name { get; set; }

        [Description("The company address")]
        public string Address { get; set; }

        [Description("The company zip")]
        public string Zip { get; set; }

        [Description("The company City")]
        public string City { get; set; }

        [Description("The company CountryCode")]
        public string CountryCode { get; set; }

        [Description("The contact Name")]
        public string ContactName { get; set; }

        [Description("The contact Email")]
        public string ContactEmail { get; set; }

        [Description("The company homepage")]
        public string Homepage { get; set; }

        [Description("The company CVR number")]
        public string CvrNumber { get; set; }

        [Description("The company registration number")]
        public string CompanyRegistrationNumber { get; set; }

        [Description("The company phonenumber")]
        public string Phone { get; set; }

        [Description("The company invoicing email")]
        public string EmailInvoice { get; set; }

        [Description("The company newsletter boolean")]
        public bool? Newsletter { get; set; }

        [Description("The company origin")]
        public string Origin { get; set; }

        [Description("The company duns number")]
        public string Duns { get; set; }

        [Description("The company Email for statements")]
        public string EmailStatement { get; set; }

        [Description("The company Email")]
        public string EmailCompany { get; set; }

        [Description("The contact person EET internal contact Email")]
        public string ContactPerson { get; set; }

        [Description("The Campaign Source name")]
        public string CampaignSource { get; set; }

        [Description("The business entity id from enrichment")]
        public int BusinessEntityId { get; set; }

        [Description("List of business lines the customer is interested in")]
        public string BusinessLines { get; set; }

        public override string GetSessionId() => LeadId.ToString();
    }
}
