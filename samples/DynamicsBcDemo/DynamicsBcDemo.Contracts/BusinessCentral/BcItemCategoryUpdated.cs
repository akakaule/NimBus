using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using NimBus.Core.Events;

namespace DynamicsBcDemo.Contracts.BusinessCentral;

[Description("Published by Business Central for an item category. CRM uses it as a product group on opportunities; the items themselves stay in Business Central. Field names follow the BC API v2.0 itemCategory resource.")]
[SessionKey(nameof(Code))]
public class BcItemCategoryUpdated : Event
{
    public static readonly BcItemCategoryUpdated Example = new()
    {
        ItemCategoryId = Guid.Parse("ca700000-0000-4000-8000-000000000001"),
        Code = "WINCH",
        DisplayName = "Winches and launch & recovery",
        ChangedAt = new DateTimeOffset(2026, 9, 28, 8, 0, 0, TimeSpan.Zero),
    };

    [Required]
    [Description("itemCategory.id")]
    public Guid ItemCategoryId { get; set; }

    [Required]
    [Description("itemCategory.code, e.g. WINCH. Session key.")]
    public string Code { get; set; } = string.Empty;

    [Required]
    [Description("itemCategory.displayName")]
    public string DisplayName { get; set; } = string.Empty;

    [Description("itemCategory.lastModifiedDateTime")]
    public DateTimeOffset ChangedAt { get; set; }
}
