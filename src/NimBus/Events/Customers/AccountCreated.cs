using NimBus.Core.Events;
using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text;

namespace NimBus.Events.Customers
{
    [Description("Triggers whenever a account is created in CRM")]
    public class AccountCreated : Event
    {
        public static AccountCreated Example = new AccountCreated()
        {
            AccountId = Guid.NewGuid(),
            LegalName = "Legal Name",
            Name = "Name",
            ExtendedLegalName = "Extended Legal Name",
            Address1_Line1 = "Address Line 1",
            Address1_Line2 = "Address Line 2",
            Address1_City = "City",
            Telephone1 = "1234567890",
            Fax = "1234567890",
            VATRegNo = "1234567890",
            CompanyRegNo = "1234567890",
            Address1_PostalCode = "1234",
            Address1_County = "AU",
            EMailAddress1 = "abc@abc.com",
            WebSiteURL = "abc.com",
            Telephone2 = "1234567890",
            NAVCustomerNumber = "1234567890",
            DUNSNo = "1234567890",
            DepartmentCode = "DK",
            LanguageCode = "en",
            CountryCode = "+00",
            SalesGroupNo = "1234567890",
            BusinessGroupNo = "1234567890",
            GeographyGroupNo = "1234567890",
            AreaGroupNo = "1234567890",
            LastmodifiedAt = DateTime.Now,
            LastmodifiedBy = "CBR",
            ShipmentEmail = "ja@mail.dk",
            OrderProcEmail = "ja@mail.dk",
            StatementEmail = "ja@mail.dk",
            InvoiceEmail = "ja@mail.dk",

        };

        [Required]
        [Description("The CRM account record unique identifier")]
        public Guid AccountId { get; set; }

        [Description("Account business unit")]
        public string BusinessUnit { get; set; }

        [Description("Account legal name")]
        public string LegalName { get; set; }

        [Description("Account name")]
        public string Name { get; set; }

        [Description("Account extended legal name")]
        public string ExtendedLegalName { get; set; }

        [Description("Account first address line")]
        public string Address1_Line1 { get; set; }

        [Description("Account second address line")]
        public string Address1_Line2 { get; set; }

        [Description("Account City")]
        public string Address1_City { get; set; }

        [Description("Account Telephone number")]
        public string Telephone1 { get; set; }

        [Description("Account Fax number")]
        public string Fax { get; set;}

        [Description("Account VAT registration number")]
        public string VATRegNo { get; set; }

        [Description("Account Company registration number")]
        public string CompanyRegNo { get; set; }

        [Description("Account postal code")]
        public string Address1_PostalCode { get; set; }

        [Description("Account county")]
        public string Address1_County { get; set; }

        [Description("Account email address")]
        public string EMailAddress1 { get; set; }

        [Description("Account website url")]
        public string WebSiteURL { get; set; }

        [Description("Account second phone number")]
        public string Telephone2 { get; set; }

        [Description("Account Nav customer number")]
        public string NAVCustomerNumber { get; set; }

        [Description("Account DUNS number")]
        public string DUNSNo { get; set; }

        [Description("Account department code")]
        public string DepartmentCode { get; set; }

        [Description("Account language code")]
        public string LanguageCode { get; set; }

        [Description("Account country code")]
        public string CountryCode { get; set; }

        [Description("Account sales group number")]
        public string SalesGroupNo { get; set; }

        [Description("Account business group number")]
        public string BusinessGroupNo { get; set; }

        [Description("Account geography group number")]
        public string GeographyGroupNo { get; set; }

        [Description("Account area group number")]
        public string AreaGroupNo { get; set; }

        [Description("Last modified By")]
        public string LastmodifiedBy { get; set; }

        [Description("Last modified At")]
        public DateTime LastmodifiedAt { get; set; }

        [Description("The Account StatementEmail")]
        public string StatementEmail { get; set; }

        [Description("The Account OrderProcEmail")]
        public string OrderProcEmail { get; set; }

        [Description("The Account ShipmentEmail")]
        public string ShipmentEmail { get; set; }

        [Description("The Account InvoiceEmail")]
        public string InvoiceEmail { get; set; }

        [Description("The Account SegmentationAdjusted")]
        public string SegmentationAdjusted { get; set; }

        [Description("The Account SalesProfitPotential")]
        public string ZZ_SalesProfitPotential { get; set; }

        [Description("The Account AutoConvert")]
        public bool AutoConvert { get; set; }

        [Description("The Account Blocking state")]
        public int Blocking { get; set; }

        [Description("The Account SalesRevenuePotential")]
        public string SalesRevenuePotential { get; set; }

        [Description("The Account SalesPersonCode")]
        public string SalesPersonCode { get; set; }

        [Description("The Account SalesPersonName")]
        public string SalesPersonName { get; set; }

        [Description("The Account SalesPersonUserId")]
        public string SalesPersonUserId { get; set; }

        [Description("The Account final SalesCategory")]
        public int? SalesCategory { get; set; }

        [Description("The Account HIPO evaluation")]
        public int HipoEvaluation { get; set; }

        public override string GetSessionId() => AccountId.ToString();
    }
}
