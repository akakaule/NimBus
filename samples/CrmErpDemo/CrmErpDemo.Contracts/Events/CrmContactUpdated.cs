using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using NimBus.Core.Events;
using NimBus.Core.Messages.PII;

namespace CrmErpDemo.Contracts.Events;

[Description("Published by CRM when a Contact is updated.")]
[SessionKey(nameof(ContactId))]
public class CrmContactUpdated : Event
{
    public static readonly CrmContactUpdated Example = new()
    {
        ContactId = Guid.Parse("9b8a7c6d-5e4f-4a3b-8c2d-1e0f9a8b7c03"),
        AccountId = Guid.Parse("6f1c0a5e-4b2d-4e8a-9c3f-1a2b3c4d5e01"),
        FirstName = "Alma",
        LastName = "Jensen-Holm",
        Email = "alma.jensen@contoso.example",
        Phone = "+45 31 12 34 56",
    };

    [Required]
    public Guid ContactId { get; set; }

    public Guid? AccountId { get; set; }

    [Required]
    public string FirstName { get; set; } = string.Empty;

    [Required]
    public string LastName { get; set; } = string.Empty;

    [EmailAddress]
    [Sensitive(Mode = MaskMode.Hash)]
    public string? Email { get; set; }

    [Sensitive(Mode = MaskMode.PartialReveal, Reveal = 3)]
    public string? Phone { get; set; }
}
