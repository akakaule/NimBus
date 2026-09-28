#pragma warning disable CA1707, CA2007
using D365Sales.Api.Data;
using D365Sales.Api.Domain;
using D365Sales.Api.Integration;
using DynamicsBcDemo.Contracts.D365Sales;
using DynamicsBcDemo.Contracts.Demo;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DynamicsBcDemo.Tests;

/// <summary>
/// The CRM side of the ownership model, and how CRM's prospect and opportunity changes are sent to
/// Business Central with deterministic MessageIds.
/// </summary>
[TestClass]
public sealed class D365SalesTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 28, 10, 0, 0, TimeSpan.Zero);

    private D365DbContext _db = null!;
    private SalesService _sales = null!;
    private CapturingPublisher _publisher = null!;
    private CrmChangePublisher _changes = null!;

    [TestInitialize]
    public void Initialize()
    {
        var options = new DbContextOptionsBuilder<D365DbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        _db = new D365DbContext(options);
        D365DatabaseInitializer.Seed(_db, Now);
        _db.SaveChanges();
        _sales = new SalesService(_db, new FixedTimeProvider(Now));
        _publisher = new CapturingPublisher();
        _changes = new CrmChangePublisher(_db, _publisher, new LastPublishedChange(), NullLogger<CrmChangePublisher>.Instance);
    }

    // MSTest disposes the test class after each test.
    public void Dispose() => _db.Dispose();

    [TestMethod]
    public async Task CustomerAccount_RefusesMasterDataEditsInCrm()
    {
        var customer = SeedData.WarmUp;

        var ex = await Assert.ThrowsExactlyAsync<D365RuleException>(() =>
            _sales.UpdateAccountAsync(customer.AccountId, Edit(customer, name: "Wingtip Marine AS")));

        Assert.AreEqual(409, ex.StatusCode);
        StringAssert.Contains(ex.Message, "owned by Business Central", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task CustomerAccount_StillAllowsCrmOwnedFields()
    {
        var customer = SeedData.WarmUp;

        var (account, notifyBc) = await _sales.UpdateAccountAsync(customer.AccountId, Edit(customer, ownerId: SeedData.MayaLindqvist.SystemUserId));

        Assert.AreEqual(SeedData.MayaLindqvist.SystemUserId, account.OwnerId);
        Assert.IsFalse(notifyBc, "Business Central is not told about CRM-owned fields of its customer.");
    }

    [TestMethod]
    public async Task ProspectWithABusinessCentralQuote_IsManagedByBusinessCentral()
    {
        var proseware = SeedData.Proseware;
        await HandToBusinessCentralAsync(proseware.AccountId);

        var ex = await Assert.ThrowsExactlyAsync<D365RuleException>(() =>
            _sales.UpdateAccountAsync(proseware.AccountId, Edit(proseware, name: "Proseware Cable Systems BV")));

        Assert.AreEqual(409, ex.StatusCode);
        StringAssert.Contains(ex.Message, "has a Business Central quote", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task ProspectEdit_WhileCrmOwnsIt_IsSentToBusinessCentral()
    {
        var proseware = SeedData.Proseware;

        var (_, notifyBc) = await _sales.UpdateAccountAsync(proseware.AccountId, Edit(proseware, name: "Proseware Cable Systems BV"));

        Assert.IsTrue(notifyBc);
    }

    [TestMethod]
    public async Task Qualify_ThenPublish_SendsTheProspectBeforeTheOpportunity_InTheAccountsSession()
    {
        var lead = SeedData.Tailspin;

        var opportunity = await _sales.QualifyLeadAsync(lead.LeadId, SeedData.AlexRivera.SystemUserId);
        await _changes.PublishProspectAsync(opportunity.CustomerId);
        await _changes.PublishOpportunityAsync(opportunity.OpportunityId);

        Assert.AreEqual(2, _publisher.Published.Count);
        var prospect = (D365ProspectUpdated)_publisher.Published[0].Event;
        var sent = (D365OpportunityUpdated)_publisher.Published[1].Event;
        Assert.IsTrue(_publisher.Published.All(p => p.SessionId == lead.QualifiedAccountId.ToString()));
        Assert.AreEqual("Hannah Okafor", prospect.PrimaryContact?.FullName);
        Assert.AreEqual(lead.QualifiedOpportunityNumber, sent.OpportunityNumber);
        Assert.AreEqual(lead.CompanyName, sent.AccountName);
        Assert.AreEqual(SeedData.AlexRivera.Email, sent.SellerEmail);
        Assert.IsNull(sent.BcCustomerId, "A prospect is not a BC customer yet.");
        StringAssert.StartsWith(_publisher.Published[0].MessageId, "prospect:", StringComparison.Ordinal);
        StringAssert.StartsWith(_publisher.Published[1].MessageId, "opportunity:", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task OpportunityChange_HasADeterministicMessageId_SoARedeliveryIsADuplicate()
    {
        var opportunity = SeedData.Opportunities.Single(o => o.Number == "OPP-10017");

        var first = await _changes.PublishOpportunityAsync(opportunity.OpportunityId);
        var redelivered = await _changes.RedeliverLastOpportunityChangeAsync();
        var later = new SalesService(_db, new FixedTimeProvider(Now.AddMinutes(1)));
        await later.UpdateOpportunityAsync(opportunity.OpportunityId, new OpportunityEdit(opportunity.Name, null, 60, SeedData.Stages.Develop, 99000m, null));
        var changed = await _changes.PublishOpportunityAsync(opportunity.OpportunityId);

        Assert.AreEqual(first, redelivered?.MessageId, "The same change delivered again keeps its MessageId.");
        Assert.AreSame(_publisher.Published[0].Event, _publisher.Published[1].Event);
        Assert.AreNotEqual(first, changed, "A new change is a new message.");
        Assert.AreEqual(99000m, ((D365OpportunityUpdated)_publisher.Published[2].Event).EstimatedValue);
    }

    [TestMethod]
    public async Task ProspectManagedByBusinessCentral_IsNotSentAgain()
    {
        await HandToBusinessCentralAsync(SeedData.Proseware.AccountId);

        var messageId = await _changes.PublishProspectAsync(SeedData.Proseware.AccountId);

        Assert.IsNull(messageId);
        Assert.AreEqual(0, _publisher.Published.Count);
    }

    [TestMethod]
    public async Task Opportunity_OfACustomer_CarriesTheBusinessCentralCustomerId()
    {
        var opportunity = SeedData.Opportunities.Single(o => o.Number == "OPP-10099");

        await _changes.PublishOpportunityAsync(opportunity.OpportunityId);

        var sent = (D365OpportunityUpdated)_publisher.Published.Single().Event;
        Assert.AreEqual(SeedData.WarmUp.BcCustomer!.CustomerId, sent.BcCustomerId);
    }

    [TestMethod]
    public async Task OpportunityEdit_WithAnUnknownProductGroup_IsRefused()
    {
        var opportunity = SeedData.Opportunities.Single(o => o.Number == "OPP-10017");

        var ex = await Assert.ThrowsExactlyAsync<D365RuleException>(() =>
            _sales.UpdateOpportunityAsync(opportunity.OpportunityId, new OpportunityEdit(opportunity.Name, null, 50, SeedData.Stages.Develop, null, Guid.NewGuid())));

        Assert.AreEqual(400, ex.StatusCode);
    }

    /// <summary>What the first Business Central quote does to the account (the adapter's Dataverse write).</summary>
    private async Task HandToBusinessCentralAsync(Guid accountId)
    {
        var account = await _db.Accounts.SingleAsync(a => a.AccountId == accountId);
        account.CsMasterDataOwner = OptionSets.MasterDataOwner.BusinessCentral;
        await _db.SaveChangesAsync();
    }

    private static AccountEdit Edit(SeedData.Account seed, string? name = null, Guid? ownerId = null) => new(
        Name: name ?? seed.Name,
        Address1Line1: seed.AddressLine1,
        Address1City: seed.City,
        Address1PostalCode: seed.PostalCode,
        Address1Country: seed.CountryCode,
        Telephone1: seed.Phone,
        WebsiteUrl: seed.Website,
        CsVatNumber: seed.VatRegistrationNumber,
        OwnerId: ownerId ?? seed.OwnerId,
        Description: null);
}
