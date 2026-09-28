using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using NimBus.Core.Messages.PII;

namespace DynamicsBcDemo.Contracts.D365Sales;

/// <summary>Prospect master data owned by CRM until the prospect becomes a buying customer.</summary>
public class ProspectDetails
{
    [Required]
    [Description("The account name.")]
    public string Name { get; set; } = string.Empty;

    // A VAT number identifies a natural person for sole traders, so only the last four
    // characters stay visible — enough to match against what a caller reads out.
    [Sensitive(Mode = MaskMode.PartialReveal, Reveal = 4)]
    [Description("VAT registration number.")]
    public string? VatRegistrationNumber { get; set; }

    [Description("Street address.")]
    public string? AddressLine1 { get; set; }

    [Description("City.")]
    public string? City { get; set; }

    [Description("Postal code.")]
    public string? PostalCode { get; set; }

    [Required]
    [Description("ISO 3166-1 alpha-2 country code.")]
    public string CountryCode { get; set; } = string.Empty;

    [Description("Main phone number.")]
    public string? Phone { get; set; }

    [Description("Web site.")]
    public string? Website { get; set; }
}

/// <summary>A contact person at a prospect or customer.</summary>
public class ContactPersonDetails
{
    [Sensitive]
    [Description("Full name of the contact person.")]
    public string? FullName { get; set; }

    [Sensitive]
    [Description("E-mail address of the contact person.")]
    public string? Email { get; set; }

    [Sensitive]
    [Description("Phone number of the contact person.")]
    public string? Phone { get; set; }
}
