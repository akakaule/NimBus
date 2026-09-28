using D365Sales.Adapter.Clients;
using DynamicsBcDemo.Contracts.BusinessCentral;
using NimBus.SDK.EventHandlers;

namespace D365Sales.Adapter.Handlers;

/// <summary>
/// Business Central created a customer — for a CRM prospect, the moment it became a buying
/// customer. The CRM account flips from Prospect to Customer and Business Central becomes the owner
/// of its master data (the fields go read-only in CRM).
/// </summary>
public sealed class BcCustomerCreatedHandler(IDataverseClient dataverse) : IEventHandler<BcCustomerCreated>
{
    public Task Handle(BcCustomerCreated message, IEventHandlerContext context, CancellationToken cancellationToken = default) =>
        CustomerMirror.ApplyAsync(dataverse, message, cancellationToken);
}

/// <summary>Business Central changed customer master data (credit limit, blocked, address): refresh the mirror.</summary>
public sealed class BcCustomerUpdatedHandler(IDataverseClient dataverse) : IEventHandler<BcCustomerUpdated>
{
    public Task Handle(BcCustomerUpdated message, IEventHandlerContext context, CancellationToken cancellationToken = default) =>
        CustomerMirror.ApplyAsync(dataverse, message, cancellationToken);
}

internal static class CustomerMirror
{
    private const int RelationshipTypeCustomer = 3;
    private const int MasterDataOwnerBusinessCentral = 2;

    public static async Task ApplyAsync(IDataverseClient dataverse, BcCustomerEvent customer, CancellationToken cancellationToken)
    {
        var columns = new Dictionary<string, object?>
        {
            ["name"] = customer.DisplayName,
            ["accountnumber"] = customer.CustomerNumber,
            ["customertypecode"] = RelationshipTypeCustomer,
            ["address1_line1"] = customer.AddressLine1,
            ["address1_city"] = customer.City,
            ["address1_postalcode"] = customer.PostalCode,
            ["address1_country"] = customer.CountryCode,
            ["telephone1"] = customer.PhoneNumber,
            ["websiteurl"] = customer.Website,
            ["cs_vatnumber"] = customer.TaxRegistrationNumber,
            ["creditlimit"] = customer.CreditLimit,
            ["creditonhold"] = !string.IsNullOrWhiteSpace(customer.Blocked),
            ["cs_bccustomerid"] = customer.CustomerId,
            ["cs_bcblocked"] = customer.Blocked,
            ["cs_bcbalancedue"] = customer.BalanceDue,
            ["cs_bcpaymentterms"] = customer.PaymentTermsCode,
            ["cs_masterdataowner"] = MasterDataOwnerBusinessCentral,
            ["cs_bclastsyncedon"] = customer.ChangedAt,
        };

        if (customer.CrmAccountId is Guid accountId)
        {
            // CRM's own account: update it in place. The owning seller stays CRM's decision.
            await dataverse.PatchAccountAsync(accountId, columns, cancellationToken);
        }
        else
        {
            // A customer created directly in Business Central (e.g. loaded at go-live): upsert by
            // the BC customer id, and let the BC salesperson decide the owning seller.
            columns["cs_bcsalespersoncode"] = customer.SalespersonCode;
            await dataverse.UpsertAccountByBcCustomerIdAsync(customer.CustomerId, columns, cancellationToken);
        }
    }
}
