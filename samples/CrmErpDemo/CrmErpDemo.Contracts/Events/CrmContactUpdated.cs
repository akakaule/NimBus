using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using NimBus.Core.Events;

namespace CrmErpDemo.Contracts.Events;

[Description("Published by CRM when a Contact is updated.")]
[SessionKey(nameof(ContactId))]
public class CrmContactUpdated : Event
{
    [Required]
    public Guid ContactId { get; set; }

    public Guid? AccountId { get; set; }

    [Required]
    public string FirstName { get; set; } = string.Empty;

    [Required]
    public string LastName { get; set; } = string.Empty;

    [EmailAddress]
    public string? Email { get; set; }

    public string? Phone { get; set; }
}
