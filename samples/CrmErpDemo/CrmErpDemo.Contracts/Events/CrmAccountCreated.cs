using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using NimBus.Core.Events;

namespace CrmErpDemo.Contracts.Events;

[Description("Published by CRM when an Account is created.")]
[SessionKey(nameof(AccountId))]
public class CrmAccountCreated : Event
{
    [Required]
    [Description("The CRM account identifier. Session key for the end-to-end flow.")]
    public Guid AccountId { get; set; }

    [Required]
    [Description("The legal name of the account.")]
    public string LegalName { get; set; } = string.Empty;

    [Description("Tax identifier (VAT, EIN, etc).")]
    public string? TaxId { get; set; }

    [Required]
    [Description("ISO 3166-1 alpha-2 country code.")]
    public string CountryCode { get; set; } = string.Empty;

    [Description("When the account was created in CRM.")]
    public DateTimeOffset CreatedAt { get; set; }
}
