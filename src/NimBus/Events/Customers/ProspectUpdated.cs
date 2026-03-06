using NimBus.Core.Events;
using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel;

namespace NimBus.Events.Customers
{
    [Description("Triggers whenever a prospect is updated in Nav09")]
    public class ProspectUpdated : Event
    {
        public static ProspectUpdated Example = new ProspectUpdated()
        {
            ProspectId = 6543,
            Name = "Name",
            Name2 = "Name",
            SearchName = "Name",
            BusinessUnit = "DK",
            AccountGUID = Guid.NewGuid(),
            ProspectNo = "123",
            Address = "Address",
            Address2 = "address2",
            City = "City",
            PhoneNo = "1234567890",
            DepartmentCode = "DK",
            LanguageCode = "en",
            SalespersonCode = "1234567890",
            CountryCode = "+00",
            FaxNo = "1234567890",
            VatRegistrationNo = "1234567890",
            PostCode = "1234",
            County = "AU",
            Email = "1234567890",
            Homepage = "1234567890",
            PhoneNo2 ="1234567890",
            CustomerNumber = "1234567890",
            LastmodifiedAt = DateTime.Now,
            LastmodifiedBy = "CBR",
        };

        [Required]
        [Description("The Nav09 prospect record unique identifier")]
        public int ProspectId { get; set; }

        [Description("The prospect CustomerNumber")]
        public string CustomerNumber { get; set; }

        [Description("The prospect BusinessUnit")]
        public string BusinessUnit { get; set; }

        [Required]
        [Description("The prospect related AccountGUID")]
        public Guid AccountGUID { get; set; }

        [Description("The prospect number")]
        public string ProspectNo { get; set; }

        [Required]
        [Description("The prospect name")]
        public string Name { get; set; }

        [Description("The prospect name2")]
        public string Name2 { get; set; }

        [Description("The prospect searchname")]
        public string SearchName { get; set; }

        [Description("The prospect address")]
        public string Address { get; set; }

        [Description("The prospect address")]
        public string Address2 { get; set; }

        [Description("The prospect city")]
        public string City { get; set; }

        [Description("The prospect phone number")]
        public string PhoneNo { get; set; }

        [Description("The prospect departmentcode")]
        public string DepartmentCode { get; set; }

        [Description("The prospect LanguageCode")]
        public string LanguageCode { get; set; }

        [Description("The prospect SalespersonCode")]
        public string SalespersonCode { get; set; }

        [Description("The prospect countrycode")]
        public string CountryCode { get; set; }

        [Description("The prospect FaxNo")]
        public string FaxNo { get; set; }

        [Description("The prospect VatRegistrationNo")]
        public string VatRegistrationNo { get; set; }

        [Description("The prospect post code")]
        public string PostCode { get; set; }

        [Description("The prospect County")]
        public string County { get; set; }

        [Description("The prospect email")]
        public string Email { get; set; }

        [Description("The prospect Homepage")]
        public string Homepage { get; set; }

        [Description("The prospect PhoneNo2")]
        public string PhoneNo2 { get; set; }

        [Description("The prospect StatementEmail")]
        public string StatementEmail { get; set; }

        [Description("The prospect SegmentationAdjusted")]
        public int SegmentationAdjusted { get; set; }

        [Description("The prospect SalesProfitPotential")]
        public int ZZ_SalesProfitPotential { get; set; }

        [Description("The prospect DunsNo")]
        public string DunsNo { get; set; }

        [Description("The prospect OrderProcEmail")]
        public string OrderProcEmail { get; set; }

        [Description("The prospect ShipmentEmail")]
        public string ShipmentEmail { get; set; }

        [Description("The prospect InvoiceEmail")]
        public string InvoiceEmail { get; set; }

        [Description("The prospect CompanyRegNo")]
        public string CompanyRegNo { get; set; }

        [Description("The prospect SalesGroupNo")]
        public string SalesGroupNo { get; set; }

        [Description("The prospect BusinessGroupNo")]
        public string BusinessGroupNo { get; set; }

        [Description("The prospect GeographyGroupNo")]
        public string GeographyGroupNo { get; set; }

        [Description("The prospect AreaGroupNo")]
        public string AreaGroupNo { get; set; }

        [Description("Last modified By")]
        public string LastmodifiedBy { get; set; }

        [Description("Last modified At")]
        public DateTime LastmodifiedAt { get; set; }

        [Description("Blocking")]
        public int Blocking { get; set; }

        [Description("SalesRevenuePotential")]
        public string SalesRevenuePotential { get; set; }

        public override string GetSessionId() => AccountGUID.ToString();
    }
}
