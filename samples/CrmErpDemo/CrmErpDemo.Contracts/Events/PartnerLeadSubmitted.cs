using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using NimBus.Core.Events;
using NimBus.Core.Messages.PII;

namespace CrmErpDemo.Contracts.Events;

[Description("Submitted by an external partner system as a raw CloudEvents 1.0 message (type com.partnerportal.crm.PartnerLeadSubmitted); no NimBus producer endpoint exists. The CRM adapter consumes it in AutoDetect mode and creates a Partner-origin contact.")]
[SessionKey(nameof(LeadId))]
public class PartnerLeadSubmitted : Event
{
    public static readonly PartnerLeadSubmitted Example = new()
    {
        LeadId = Guid.Parse("4d3c2b1a-0f9e-4d8c-8b7a-6f5e4d3c2b04"),
        FirstName = "Noah",
        LastName = "Petersen",
        Email = "noah.petersen@partner.example",
        Phone = "+45 20 11 22 33",
        CompanyName = "Fabrikam A/S",
    };

    [Required]
    public Guid LeadId { get; set; }

    [Required]
    public string FirstName { get; set; } = string.Empty;

    [Required]
    public string LastName { get; set; } = string.Empty;

    [EmailAddress]
    [Sensitive(Mode = MaskMode.Hash)]
    public string? Email { get; set; }

    [Sensitive(Mode = MaskMode.PartialReveal, Reveal = 3)]
    public string? Phone { get; set; }

    [Description("Name of the company the lead works for, as known by the partner system.")]
    public string? CompanyName { get; set; }
}
