using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using NimBus.Core.Events;

namespace CrmErpDemo.Contracts.Events;

[Description("Published by CRM when an Account is updated.")]
[SessionKey(nameof(AccountId))]
public class CrmAccountUpdated : Event
{
    [Required]
    public Guid AccountId { get; set; }

    [Required]
    public string LegalName { get; set; } = string.Empty;

    public string? TaxId { get; set; }

    [Required]
    public string CountryCode { get; set; } = string.Empty;

    [Description("When the update occurred in CRM.")]
    public DateTimeOffset UpdatedAt { get; set; }
}
