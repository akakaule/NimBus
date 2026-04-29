using Crm.Api.Entities;
using CrmErpDemo.Contracts.Events;

namespace Crm.Api.Mapping;

public static class ContactMapper
{
    public static CrmContactCreated ToCreatedEvent(Contact c) => new()
    {
        ContactId = c.Id,
        AccountId = c.AccountId,
        FirstName = c.FirstName,
        LastName = c.LastName,
        Email = c.Email,
        Phone = c.Phone,
    };

    public static CrmContactUpdated ToUpdatedEvent(Contact c) => new()
    {
        ContactId = c.Id,
        AccountId = c.AccountId,
        FirstName = c.FirstName,
        LastName = c.LastName,
        Email = c.Email,
        Phone = c.Phone,
    };

    public static CrmContactDeleted ToDeletedEvent(Contact c) => new()
    {
        ContactId = c.Id,
        DeletedAt = c.UpdatedAt ?? DateTimeOffset.UtcNow,
    };
}
