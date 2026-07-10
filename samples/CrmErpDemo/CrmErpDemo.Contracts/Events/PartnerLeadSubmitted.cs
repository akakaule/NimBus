using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using NimBus.Core.Events;

namespace CrmErpDemo.Contracts.Events;

[Description("Submitted by an external partner system as a raw CloudEvents 1.0 message (type com.partnerportal.crm.PartnerLeadSubmitted); no NimBus producer endpoint exists. The CRM adapter consumes it in AutoDetect mode and creates a Partner-origin contact.")]
[SessionKey(nameof(LeadId))]
public class PartnerLeadSubmitted : Event
{
    [Required]
    public Guid LeadId { get; set; }

    [Required]
    public string FirstName { get; set; } = string.Empty;

    [Required]
    public string LastName { get; set; } = string.Empty;

    [EmailAddress]
    public string? Email { get; set; }

    public string? Phone { get; set; }

    [Description("Name of the company the lead works for, as known by the partner system.")]
    public string? CompanyName { get; set; }
}
