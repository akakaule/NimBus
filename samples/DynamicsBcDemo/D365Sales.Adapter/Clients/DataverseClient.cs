using System.Globalization;
using System.Net.Http.Json;
using DynamicsBcDemo.Contracts.Http;

namespace D365Sales.Adapter.Clients;

/// <summary>
/// The Dataverse Web API calls the adapter makes: PATCH by id, upserts by alternate key, and the
/// WinOpportunity action. Column names are Dataverse logical names; lookups use <c>@odata.bind</c>.
/// Pointing it at a real environment means adding an access token (client credentials for an
/// application user) and the org URL.
/// </summary>
public interface IDataverseClient
{
    /// <summary><c>PATCH accounts({accountId})</c>.</summary>
    Task PatchAccountAsync(Guid accountId, IDictionary<string, object?> columns, CancellationToken cancellationToken);

    /// <summary><c>PATCH accounts(cs_bccustomerid={bcCustomerId})</c> — upsert by alternate key.</summary>
    Task UpsertAccountByBcCustomerIdAsync(Guid bcCustomerId, IDictionary<string, object?> columns, CancellationToken cancellationToken);

    /// <summary><c>PATCH opportunities({opportunityId})</c>.</summary>
    Task PatchOpportunityAsync(Guid opportunityId, IDictionary<string, object?> columns, CancellationToken cancellationToken);

    /// <summary><c>PATCH cs_bcquotes(cs_bcquoteid={bcQuoteId})</c> — upsert of the quote mirror.</summary>
    Task UpsertBcQuoteAsync(Guid bcQuoteId, IDictionary<string, object?> columns, CancellationToken cancellationToken);

    /// <summary><c>PATCH contacts(cs_bccontactid={bcContactId})</c> — upsert of a contact Business Central owns.</summary>
    Task UpsertContactByBcContactIdAsync(Guid bcContactId, IDictionary<string, object?> columns, CancellationToken cancellationToken);

    /// <summary><c>PATCH cs_productgroups(cs_productgroupid={productGroupId})</c> — upsert of a product group.</summary>
    Task UpsertProductGroupAsync(Guid productGroupId, IDictionary<string, object?> columns, CancellationToken cancellationToken);

    /// <summary><c>POST WinOpportunity</c>.</summary>
    Task WinOpportunityAsync(Guid opportunityId, decimal actualRevenue, DateTime actualEnd, string subject, CancellationToken cancellationToken);
}

public sealed class DataverseClient(HttpClient http) : IDataverseClient
{
    private const string ApiName = "Dataverse API";
    private const string Root = "/api/data/v9.2";

    public Task PatchAccountAsync(Guid accountId, IDictionary<string, object?> columns, CancellationToken cancellationToken) =>
        PatchAsync($"{Root}/accounts({accountId})", columns, $"Update account {accountId}", cancellationToken);

    public Task UpsertAccountByBcCustomerIdAsync(Guid bcCustomerId, IDictionary<string, object?> columns, CancellationToken cancellationToken) =>
        PatchAsync($"{Root}/accounts(cs_bccustomerid={bcCustomerId})", columns, $"Upsert the account for Business Central customer {bcCustomerId}", cancellationToken);

    public Task PatchOpportunityAsync(Guid opportunityId, IDictionary<string, object?> columns, CancellationToken cancellationToken) =>
        PatchAsync($"{Root}/opportunities({opportunityId})", columns, $"Update opportunity {opportunityId}", cancellationToken);

    public Task UpsertBcQuoteAsync(Guid bcQuoteId, IDictionary<string, object?> columns, CancellationToken cancellationToken) =>
        PatchAsync($"{Root}/cs_bcquotes(cs_bcquoteid={bcQuoteId})", columns, $"Upsert the mirror of Business Central quote {bcQuoteId}", cancellationToken);

    public Task UpsertContactByBcContactIdAsync(Guid bcContactId, IDictionary<string, object?> columns, CancellationToken cancellationToken) =>
        PatchAsync($"{Root}/contacts(cs_bccontactid={bcContactId})", columns, $"Upsert the contact for Business Central contact {bcContactId}", cancellationToken);

    public Task UpsertProductGroupAsync(Guid productGroupId, IDictionary<string, object?> columns, CancellationToken cancellationToken) =>
        PatchAsync($"{Root}/cs_productgroups(cs_productgroupid={productGroupId})", columns, $"Upsert product group {productGroupId}", cancellationToken);

    public async Task WinOpportunityAsync(Guid opportunityId, decimal actualRevenue, DateTime actualEnd, string subject, CancellationToken cancellationToken)
    {
        var body = new Dictionary<string, object?>
        {
            ["Status"] = 3,
            ["OpportunityClose"] = new Dictionary<string, object?>
            {
                ["opportunityid@odata.bind"] = $"/opportunities({opportunityId})",
                ["subject"] = subject,
                ["actualrevenue"] = actualRevenue,
                ["actualend"] = actualEnd.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            },
        };

        using var response = await http.PostAsJsonAsync($"{Root}/WinOpportunity", body, cancellationToken);
        await response.EnsureSuccessOrThrowAsync($"Win opportunity {opportunityId}", ApiName, cancellationToken);
    }

    private async Task PatchAsync(string path, IDictionary<string, object?> columns, string operation, CancellationToken cancellationToken)
    {
        using var response = await http.PatchAsJsonAsync(path, columns, cancellationToken);
        await response.EnsureSuccessOrThrowAsync(operation, ApiName, cancellationToken);
    }
}
