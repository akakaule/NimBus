using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using NimBus.Core.Events;

namespace CrmErpDemo.Contracts.Events;

[Description("Published by ERP when a Contact is soft-deleted.")]
[SessionKey(nameof(ContactId))]
public class ErpContactDeleted : Event
{
    [Required]
    public Guid ContactId { get; set; }

    [Description("When the deletion occurred in ERP.")]
    public DateTimeOffset DeletedAt { get; set; }
}
