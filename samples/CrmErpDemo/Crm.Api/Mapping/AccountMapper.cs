using Crm.Api.Entities;
using CrmErpDemo.Contracts.Events;

namespace Crm.Api.Mapping;

public static class AccountMapper
{
    public static CrmAccountCreated ToCreatedEvent(Account a) => new()
    {
        AccountId = a.Id,
        LegalName = a.LegalName,
        TaxId = a.TaxId,
        CountryCode = a.CountryCode,
        CreatedAt = a.CreatedAt,
    };

    public static CrmAccountUpdated ToUpdatedEvent(Account a) => new()
    {
        AccountId = a.Id,
        ErpCustomerId = a.ErpCustomerId,
        LegalName = a.LegalName,
        TaxId = a.TaxId,
        CountryCode = a.CountryCode,
        UpdatedAt = a.UpdatedAt ?? DateTimeOffset.UtcNow,
    };

    public static CrmAccountDeleted ToDeletedEvent(Account a) => new()
    {
        AccountId = a.Id,
        ErpCustomerId = a.ErpCustomerId,
        DeletedAt = a.UpdatedAt ?? DateTimeOffset.UtcNow,
    };
}
