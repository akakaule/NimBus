using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using NimBus.Core.Events;

namespace CrmErpDemo.Contracts.Events;

[Description("Published by CRM when a Contact is created.")]
[SessionKey(nameof(ContactId))]
public class CrmContactCreated : Event
{
    [Required]
    public Guid ContactId { get; set; }

    [Description("The CRM account this contact belongs to.")]
    public Guid? AccountId { get; set; }

    [Required]
    public string FirstName { get; set; } = string.Empty;

    [Required]
    public string LastName { get; set; } = string.Empty;

    [EmailAddress]
    public string? Email { get; set; }

    public string? Phone { get; set; }
}
