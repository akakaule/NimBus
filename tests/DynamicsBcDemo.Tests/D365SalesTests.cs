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

/// <summary>The CRM side of the ownership model and the double-click protection of quote requests.</summary>
[TestClass]
public sealed class D365SalesTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 28, 10, 0, 0, TimeSpan.Zero);

    private D365DbContext _db = null!;
    private SalesService _sales = null!;

    [TestInitialize]
    public void Initialize()
    {
        var options = new DbContextOptionsBuilder<D365DbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        _db = new D365DbContext(options);
        D365DatabaseInitializer.Seed(_db, Now);
        _db.SaveChanges();
        _sales = new SalesService(_db, new FixedTimeProvider(Now));
    }

    // MSTest disposes the test class after each test.
    public void Dispose() => _db.Dispose();

    [TestMethod]
    public async Task AccountOwnedByBusinessCentral_RefusesMasterDataEditsInCrm()
    {
        var fabrikam = SeedData.Fabrikam;

        var ex = await Assert.ThrowsExactlyAsync<D365RuleException>(() =>
            _sales.UpdateAccountAsync(fabrikam.AccountId, Edit(fabrikam, name: "Fabrikam Offshore Energy AS")));

        Assert.AreEqual(409, ex.StatusCode);
        StringAssert.Contains(ex.Message, "owned by Business Central", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task AccountOwnedByBusinessCentral_StillAllowsCrmOwnedFields()
    {
        var fabrikam = SeedData.Fabrikam;

        var (account, notifyBc) = await _sales.UpdateAccountAsync(fabrikam.AccountId, Edit(fabrikam, ownerId: SeedData.MayaLindqvist.SystemUserId));

        Assert.AreEqual(SeedData.MayaLindqvist.SystemUserId, account.OwnerId);
        Assert.IsFalse(notifyBc, "Business Central is not told about CRM-owned fields of its customer.");
    }

    [TestMethod]
    public async Task ProspectEdit_IsSentToBusinessCentral_OnlyOnceBusinessCentralKnowsTheProspect()
    {
        var proseware = SeedData.Proseware;

        var (_, beforeQuote) = await _sales.UpdateAccountAsync(proseware.AccountId, Edit(proseware, name: "Proseware Cable Systems BV"));
        var account = await _db.Accounts.SingleAsync(a => a.AccountId == proseware.AccountId);
        account.CsBcContactNumber = "CT000101";
        await _db.SaveChangesAsync();
        var (_, afterQuote) = await _sales.UpdateAccountAsync(proseware.AccountId, Edit(proseware, name: "Proseware Cable Systems B.V."));

        Assert.IsFalse(beforeQuote, "A CRM-only prospect stays in CRM.");
        Assert.IsTrue(afterQuote);
    }

    [TestMethod]
    public async Task QuoteRequest_UsesADeterministicMessageId_SoADoubleClickIsADuplicate()
    {
        var publisher = new CapturingPublisher();
        var quotes = new QuoteRequestPublisher(_db, publisher, new FixedTimeProvider(Now), NullLogger<QuoteRequestPublisher>.Instance);
        var opportunity = SeedData.Opportunities.Single(o => o.Number == "OPP-10017");

        var first = await quotes.RequestAsync(opportunity.OpportunityId, SeedData.MayaLindqvist.SystemUserId);
        var second = await quotes.RequestAsync(opportunity.OpportunityId, SeedData.MayaLindqvist.SystemUserId);

        Assert.AreEqual(first.MessageId, second.MessageId);
        Assert.IsTrue(second.Repeat);
        Assert.AreEqual($"quote:{opportunity.OpportunityId:N}:1", first.MessageId);
        Assert.AreEqual(2, publisher.Published.Count);
        Assert.IsTrue(publisher.Published.All(p => p.SessionId == opportunity.AccountId.ToString()));

        var command = (CreateBcSalesQuote)publisher.Published[0].Event;
        Assert.AreEqual(SeedData.MayaLindqvist.Email, command.SellerEmail);
        Assert.IsNull(command.BcCustomerNumber, "A prospect is quoted as a contact.");
        Assert.AreEqual(opportunity.Lines.Count, command.Lines.Count);
    }

    [TestMethod]
    public async Task QuoteRequest_AfterTheLinesChange_IsANewRequest()
    {
        var publisher = new CapturingPublisher();
        var quotes = new QuoteRequestPublisher(_db, publisher, new FixedTimeProvider(Now), NullLogger<QuoteRequestPublisher>.Instance);
        var opportunity = SeedData.Opportunities.Single(o => o.Number == "OPP-10017");

        var first = await quotes.RequestAsync(opportunity.OpportunityId, null);
        await _sales.SetLinesAsync(opportunity.OpportunityId, [new OpportunityLineEdit("CBL-TOW100", 10m)]);
        var second = await quotes.RequestAsync(opportunity.OpportunityId, null);

        Assert.AreNotEqual(first.MessageId, second.MessageId);
        Assert.AreEqual(first.Revision + 1, second.Revision);
    }

    [TestMethod]
    public async Task QuoteRequest_ForAnExistingCustomer_CarriesTheBusinessCentralCustomerNumber()
    {
        var publisher = new CapturingPublisher();
        var quotes = new QuoteRequestPublisher(_db, publisher, new FixedTimeProvider(Now), NullLogger<QuoteRequestPublisher>.Instance);
        var opportunity = SeedData.Opportunities.Single(o => o.Number == "OPP-10011");

        await quotes.RequestAsync(opportunity.OpportunityId, null);

        var command = (CreateBcSalesQuote)publisher.Published.Single().Event;
        Assert.AreEqual(SeedData.Fabrikam.BcCustomer!.Number, command.BcCustomerNumber);
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
