using D365Sales.Adapter.Clients;
using DynamicsBcDemo.Contracts.BusinessCentral;
using NimBus.SDK.EventHandlers;

namespace D365Sales.Adapter.Handlers;

/// <summary>
/// A Business Central person contact of a customer (the initial sync at go-live): create or update the
/// CRM contact under the customer's account, matched on the BC contact id. The contact always arrives
/// after its customer — same session — so the account exists by then.
/// </summary>
public sealed class BcContactUpdatedHandler(IDataverseClient dataverse) : IEventHandler<BcContactUpdated>
{
    public async Task Handle(BcContactUpdated message, IEventHandlerContext context, CancellationToken cancellationToken = default)
    {
        var columns = new Dictionary<string, object?>
        {
            ["firstname"] = message.FirstName,
            ["lastname"] = message.Surname,
            ["emailaddress1"] = message.Email,
            ["telephone1"] = message.PhoneNumber,
            ["jobtitle"] = message.JobTitle,
        };

        // The account by CRM id when BC knows it, otherwise by the Business Central customer id
        // (Dataverse alternate key) — a customer loaded at go-live has no CRM id in BC yet.
        if (message.CrmAccountId is Guid crmAccountId)
            columns["parentcustomerid_account@odata.bind"] = $"/accounts({crmAccountId})";
        else if (message.CustomerId is Guid customerId)
            columns["parentcustomerid_account@odata.bind"] = $"/accounts(cs_bccustomerid={customerId})";

        await dataverse.UpsertContactByBcContactIdAsync(message.ContactId, columns, cancellationToken);
    }
}

/// <summary>A Business Central item category becomes (or updates) a CRM product group.</summary>
public sealed class BcItemCategoryUpdatedHandler(IDataverseClient dataverse) : IEventHandler<BcItemCategoryUpdated>
{
    public Task Handle(BcItemCategoryUpdated message, IEventHandlerContext context, CancellationToken cancellationToken = default) =>
        dataverse.UpsertProductGroupAsync(
            message.ItemCategoryId,
            new Dictionary<string, object?>
            {
                ["cs_code"] = message.Code,
                ["cs_name"] = message.DisplayName,
            },
            cancellationToken);
}
