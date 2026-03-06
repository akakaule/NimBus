using NimBus.Core.Events;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text;

namespace NimBus.Events.Customers
{
    [Description("Triggers whenever a vendor is created in Nav09")]
    public class VendorCreated : Event
    {
        public static VendorCreated Example = new VendorCreated()
        {
            Id = 1234,
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
            PhoneNo2 = "1234567890",            
            LastmodifiedAt = DateTime.Now,
            LastmodifiedBy = "CBR",
        };

        [Required]
        [Description("The Nav09 vendor record unique identifier")]
        public int Id { get; set; }

        [Description("The vendor BusinessUnit")]
        public string BusinessUnit { get; set; }

        [Description("The vendor CustomerNumber")]
        public string CustomerNumber { get; set; }

        [Required]
        [Description("The vendor related AccountGUID")]
        public Guid AccountGUID { get; set; }

        [Description("The vendor number")]
        public string ProspectNo { get; set; }

        [Required]
        [Description("The vendor name")]
        public string Name { get; set; }

        [Description("The vendor name2")]
        public string Name2 { get; set; }

        [Description("The vendor searchname")]
        public string SearchName { get; set; }

        [Description("The vendor address")]
        public string Address { get; set; }

        [Description("The vendor address")]
        public string Address2 { get; set; }

        [Description("The vendor city")]
        public string City { get; set; }

        [Description("The vendor phone number")]
        public string PhoneNo { get; set; }

        [Description("The vendor departmentcode")]
        public string DepartmentCode { get; set; }

        [Description("The vendor LanguageCode")]
        public string LanguageCode { get; set; }

        [Description("The vendor SalespersonCode")]
        public string SalespersonCode { get; set; }

        [Description("The vendor countrycode")]
        public string CountryCode { get; set; }

        [Description("The vendor FaxNo")]
        public string FaxNo { get; set; }

        [Description("The vendor VatRegistrationNo")]
        public string VatRegistrationNo { get; set; }

        [Description("The vendor post code")]
        public string PostCode { get; set; }

        [Description("The vendor County")]
        public string County { get; set; }

        [Description("The vendor email")]
        public string Email { get; set; }

        [Description("The vendor Homepage")]
        public string Homepage { get; set; }

        [Description("The vendor PhoneNo2")]
        public string PhoneNo2 { get; set; }

        [Description("The vendor StatementEmail")]
        public string StatementEmail { get; set; }

        [Description("The vendor SegmentationAdjusted")]
        public int SegmentationAdjusted { get; set; }

        [Description("The vendor SalesProfitPotential")]
        public int ZZ_SalesProfitPotential { get; set; }

        [Description("The vendor DunsNo")]
        public string DunsNo { get; set; }

        [Description("The vendor OrderProcEmail")]
        public string OrderProcEmail { get; set; }

        [Description("The vendor ShipmentEmail")]
        public string ShipmentEmail { get; set; }

        [Description("The vendor InvoiceEmail")]
        public string InvoiceEmail { get; set; }

        [Description("The vendor CompanyRegNo")]
        public string CompanyRegNo { get; set; }

        [Description("The vendor SalesGroupNo")]
        public string SalesGroupNo { get; set; }

        [Description("The vendor BusinessGroupNo")]
        public string BusinessGroupNo { get; set; }

        [Description("The vendor GeographyGroupNo")]
        public string GeographyGroupNo { get; set; }

        [Description("The vendor AreaGroupNo")]
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
