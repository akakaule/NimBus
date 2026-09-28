namespace D365Sales.Api.Data;

// A small slice of the Dataverse model behind Dynamics 365 Sales. Property names are the PascalCase
// form of the Dataverse logical names (accountnumber → AccountNumber); columns prefixed Cs are
// custom columns ("cs_" publisher prefix) a real solution would add for the Business Central link.

/// <summary>Dataverse option-set values the simulator uses.</summary>
public static class OptionSets
{
    /// <summary>account.customertypecode (Relationship Type).</summary>
    public static class RelationshipType
    {
        public const int Customer = 3;
        public const int Prospect = 8;
    }

    /// <summary>opportunity / lead statecode.</summary>
    public static class State
    {
        public const int Open = 0;
        public const int WonOrQualified = 1;
        public const int LostOrDisqualified = 2;
    }

    /// <summary>account.cs_masterdataowner: which system owns the account's master data.</summary>
    public static class MasterDataOwner
    {
        public const int Dynamics365 = 1;
        public const int BusinessCentral = 2;
    }
}

/// <summary>systemuser: a seller.</summary>
public class SystemUser
{
    public Guid SystemUserId { get; set; }
    public string FullName { get; set; } = string.Empty;
    public string InternalEmailAddress { get; set; } = string.Empty;
}

/// <summary>product: CRM's copy of the Business Central item list.</summary>
public class Product
{
    public Guid ProductId { get; set; }
    public string ProductNumber { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public string DefaultUnit { get; set; } = "PCS";
}

/// <summary>account.</summary>
public class Account
{
    public Guid AccountId { get; set; }
    public string Name { get; set; } = string.Empty;

    /// <summary>accountnumber: the Business Central customer number once the account is a customer.</summary>
    public string? AccountNumber { get; set; }

    /// <summary>customertypecode (Relationship Type): 8 Prospect, 3 Customer.</summary>
    public int CustomerTypeCode { get; set; } = OptionSets.RelationshipType.Prospect;

    public string? Address1Line1 { get; set; }
    public string? Address1City { get; set; }
    public string? Address1PostalCode { get; set; }
    public string? Address1Country { get; set; }
    public string? Telephone1 { get; set; }
    public string? WebsiteUrl { get; set; }
    public string? CsVatNumber { get; set; }

    /// <summary>creditlimit / creditonhold: mirrored from Business Central.</summary>
    public decimal? CreditLimit { get; set; }
    public bool CreditOnHold { get; set; }

    public Guid OwnerId { get; set; }
    public Guid? PrimaryContactId { get; set; }

    /// <summary>cs_bccustomerid: the Business Central customer (customers resource id).</summary>
    public Guid? CsBcCustomerId { get; set; }

    /// <summary>cs_bccontactnumber: the Business Central prospect contact, once BC knows the prospect.</summary>
    public string? CsBcContactNumber { get; set; }

    public string? CsBcBlocked { get; set; }
    public decimal? CsBcBalanceDue { get; set; }
    public string? CsBcPaymentTerms { get; set; }

    /// <summary>cs_masterdataowner: 1 Dynamics 365, 2 Business Central.</summary>
    public int CsMasterDataOwner { get; set; } = OptionSets.MasterDataOwner.Dynamics365;

    public DateTimeOffset? CsBcLastSyncedOn { get; set; }
    public string? Description { get; set; }
    public DateTimeOffset CreatedOn { get; set; }
    public DateTimeOffset ModifiedOn { get; set; }
}

/// <summary>contact.</summary>
public class Contact
{
    public Guid ContactId { get; set; }
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string? EmailAddress1 { get; set; }
    public string? Telephone1 { get; set; }
    public string? JobTitle { get; set; }
    public Guid? ParentCustomerId { get; set; }
    public DateTimeOffset CreatedOn { get; set; }

    public string FullName => $"{FirstName} {LastName}".Trim();
}

/// <summary>lead.</summary>
public class Lead
{
    public Guid LeadId { get; set; }
    public string Subject { get; set; } = string.Empty;
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string CompanyName { get; set; } = string.Empty;
    public string? EmailAddress1 { get; set; }
    public string? Telephone1 { get; set; }
    public string? JobTitle { get; set; }
    public string? Address1Line1 { get; set; }
    public string? Address1City { get; set; }
    public string? Address1PostalCode { get; set; }
    public string? Address1Country { get; set; }
    public string? WebsiteUrl { get; set; }
    public string? CsVatNumber { get; set; }
    public decimal? EstimatedValue { get; set; }

    /// <summary>statecode: 0 Open, 1 Qualified, 2 Disqualified.</summary>
    public int StateCode { get; set; }

    public Guid OwnerId { get; set; }
    public DateTimeOffset CreatedOn { get; set; }

    /// <summary>Ids the qualify step uses, pre-set for seeded leads so demo deep links stay stable.</summary>
    public Guid? QualifiedAccountId { get; set; }
    public Guid? QualifiedContactId { get; set; }
    public Guid? QualifiedOpportunityId { get; set; }
    public string? QualifiedOpportunityNumber { get; set; }
}

/// <summary>opportunity.</summary>
public class Opportunity
{
    public Guid OpportunityId { get; set; }

    /// <summary>cs_number: an autonumber column (OPP-10011).</summary>
    public string CsNumber { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>customerid: the account.</summary>
    public Guid CustomerId { get; set; }

    public Guid? ParentContactId { get; set; }
    public decimal? EstimatedValue { get; set; }
    public DateTime? EstimatedCloseDate { get; set; }
    public int CloseProbability { get; set; }

    /// <summary>stepname: the business-process-flow stage, e.g. 2-Develop.</summary>
    public string StepName { get; set; } = "1-Qualify";

    /// <summary>statecode: 0 Open, 1 Won, 2 Lost.</summary>
    public int StateCode { get; set; }

    public decimal? ActualValue { get; set; }
    public DateTime? ActualCloseDate { get; set; }
    public Guid OwnerId { get; set; }

    public Guid? CsBcQuoteId { get; set; }
    public string? CsBcQuoteNumber { get; set; }

    /// <summary>cs_bcquotestatus: Requested, then Business Central's Draft / Sent / Accepted / Expired.</summary>
    public string? CsBcQuoteStatus { get; set; }

    public string? CsBcOrderNumber { get; set; }

    /// <summary>Bumped whenever the product lines change; part of the quote request's MessageId.</summary>
    public int CsLinesRevision { get; set; } = 1;

    public int CsQuoteRequestRevision { get; set; }
    public DateTimeOffset? CsQuoteRequestedOn { get; set; }
    public DateTimeOffset CreatedOn { get; set; }
    public DateTimeOffset ModifiedOn { get; set; }
    public List<OpportunityProduct> Lines { get; set; } = [];
}

/// <summary>opportunityproduct: a product line.</summary>
public class OpportunityProduct
{
    public Guid OpportunityProductId { get; set; }
    public Guid OpportunityId { get; set; }
    public int Sequence { get; set; }
    public string ProductNumber { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public decimal Quantity { get; set; }
    public decimal PricePerUnit { get; set; }
    public decimal ExtendedAmount { get; set; }
}

/// <summary>cs_bcquote: a read-only mirror of a Business Central sales quote.</summary>
public class BcQuoteMirror
{
    /// <summary>cs_bcquoteid: the Business Central quote id (alternate key).</summary>
    public Guid BcQuoteId { get; set; }

    public string QuoteNumber { get; set; } = string.Empty;
    public Guid? OpportunityId { get; set; }
    public Guid? AccountId { get; set; }
    public string Status { get; set; } = string.Empty;
    public decimal TotalAmount { get; set; }
    public string CurrencyCode { get; set; } = "EUR";
    public DateTime? ValidUntil { get; set; }
    public DateTimeOffset? SentOn { get; set; }
    public DateTime? AcceptedOn { get; set; }
    public DateTimeOffset LastSyncedOn { get; set; }
}

/// <summary>A timeline entry (like a note or a system post) on an account, opportunity or lead.</summary>
public class TimelineEntry
{
    public Guid Id { get; set; }
    public Guid RegardingId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Detail { get; set; }

    /// <summary>User, Integration or System.</summary>
    public string Source { get; set; } = "User";

    public DateTimeOffset CreatedOn { get; set; }
}
