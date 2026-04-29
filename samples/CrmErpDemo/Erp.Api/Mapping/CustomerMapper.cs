using CrmErpDemo.Contracts.Events;
using Erp.Api.Entities;

namespace Erp.Api.Mapping;

public static class CustomerMapper
{
    public static ErpCustomerCreated ToCreatedEvent(Customer c) => new()
    {
        Origin = c.CrmAccountId.HasValue ? CustomerOrigin.Crm : CustomerOrigin.Erp,
        AccountId = c.CrmAccountId ?? c.Id,
        ErpCustomerId = c.Id,
        CustomerNumber = c.CustomerNumber,
        LegalName = c.LegalName,
        TaxId = c.TaxId,
        CountryCode = c.CountryCode,
    };

    public static ErpContactCreated ToContactCreatedEvent(ErpContact c) => new()
    {
        ContactId = c.Id,
        CustomerId = c.CustomerId,
        FirstName = c.FirstName,
        LastName = c.LastName,
        Email = c.Email,
        Phone = c.Phone,
    };

    public static ErpContactUpdated ToContactUpdatedEvent(ErpContact c) => new()
    {
        ContactId = c.Id,
        CustomerId = c.CustomerId,
        FirstName = c.FirstName,
        LastName = c.LastName,
        Email = c.Email,
        Phone = c.Phone,
    };

    public static ErpCustomerUpdated ToUpdatedEvent(Customer c) => new()
    {
        AccountId = c.CrmAccountId ?? c.Id,
        ErpCustomerId = c.Id,
        CustomerNumber = c.CustomerNumber,
        LegalName = c.LegalName,
        TaxId = c.TaxId,
        CountryCode = c.CountryCode,
    };

    public static ErpCustomerDeleted ToDeletedEvent(Customer c) => new()
    {
        AccountId = c.CrmAccountId ?? c.Id,
        ErpCustomerId = c.Id,
        DeletedAt = c.UpdatedAt ?? DateTimeOffset.UtcNow,
    };

    public static ErpContactDeleted ToContactDeletedEvent(ErpContact c) => new()
    {
        ContactId = c.Id,
        DeletedAt = c.UpdatedAt ?? DateTimeOffset.UtcNow,
    };
}
