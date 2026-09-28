namespace DynamicsBcDemo.Contracts.Demo;

/// <summary>
/// Deterministic, fictional seed data shared by both simulators, so Dynamics 365 Sales and
/// Business Central start consistent without a startup sync: the existing BC customers are
/// already linked to CRM accounts, as if the pilot office's initial load had run.
/// Fixed ids keep nimbus-ops deep links (<c>?sessionId=&lt;accountId&gt;</c>) stable between runs.
/// Every company, person and product here is fictional.
/// </summary>
public static class SeedData
{
    /// <summary>The fictional company running both systems.</summary>
    public const string CompanyName = "Contoso Subsea";

    /// <summary>The BC company id used in API v2.0 routes: /companies({CompanyId})/...</summary>
    public static readonly Guid CompanyId = Guid.Parse("3f2b7c1e-5a4d-4e8f-9b61-0c7d2e9a4b10");

    /// <summary>Currency used throughout the demo.</summary>
    public const string CurrencyCode = "EUR";

    /// <summary>The customer template BC applies when a prospect contact becomes a customer.</summary>
    public const string DefaultCustomerTemplate = "BUSINESS";

    /// <summary>Opportunity business-process-flow stages (Dataverse <c>stepname</c>).</summary>
    public static class Stages
    {
        public const string Qualify = "1-Qualify";
        public const string Develop = "2-Develop";
        public const string Propose = "3-Propose";
        public const string Close = "4-Close";
    }

    /// <summary>A seller: a D365 user and, when <see cref="BcSalespersonCode"/> is set, a BC salesperson.</summary>
    public sealed record Seller(Guid SystemUserId, string FullName, string Email, string? BcSalespersonCode);

    /// <summary>A catalog item, identical in both systems (BC is the master; CRM holds a copy).</summary>
    public sealed record Item(Guid ItemId, string Number, string DisplayName, decimal UnitPrice, string UnitOfMeasure, bool BlockedInBc);

    /// <summary>A contact person at an account.</summary>
    public sealed record Person(Guid ContactId, string FirstName, string LastName, string Email, string Phone, string JobTitle);

    /// <summary>A CRM account; <see cref="BcCustomer"/> is set when the account is already a BC customer.</summary>
    public sealed record Account(
        Guid AccountId,
        string Name,
        string AddressLine1,
        string City,
        string PostalCode,
        string CountryCode,
        string Phone,
        string Website,
        string VatRegistrationNumber,
        Guid OwnerId,
        Person PrimaryContact,
        BcCustomer? BcCustomer);

    /// <summary>The BC side of an account that is already a buying customer.</summary>
    public sealed record BcCustomer(Guid CustomerId, string Number, decimal CreditLimit, decimal BalanceDue, decimal OverdueAmount, string PaymentTermsCode, string Blocked);

    /// <summary>An open CRM opportunity with product lines.</summary>
    public sealed record Opportunity(
        Guid OpportunityId,
        string Number,
        string Name,
        Guid AccountId,
        Guid OwnerId,
        string Stage,
        int CloseInDays,
        int Probability,
        IReadOnlyList<(string ItemNumber, decimal Quantity)> Lines,
        SentQuote? Quote);

    /// <summary>A quote BC already sent for a seeded opportunity.</summary>
    public sealed record SentQuote(Guid QuoteId, string Number, int SentDaysAgo, int ValidForDays);

    /// <summary>An unqualified CRM lead. Qualifying it creates the account, contact and opportunity with the fixed ids given here.</summary>
    public sealed record Lead(
        Guid LeadId,
        string Topic,
        string CompanyName,
        string FirstName,
        string LastName,
        string Email,
        string Phone,
        string JobTitle,
        string AddressLine1,
        string City,
        string PostalCode,
        string CountryCode,
        string Website,
        string VatRegistrationNumber,
        decimal EstimatedValue,
        Guid OwnerId,
        Guid QualifiedAccountId,
        Guid QualifiedContactId,
        Guid QualifiedOpportunityId,
        string QualifiedOpportunityNumber);

    // ---- Sellers -------------------------------------------------------------------------

    public static readonly Seller AlexRivera = new(G("5e11e700", 1), "Alex Rivera", "alex.rivera@contososubsea.example", "AR");
    public static readonly Seller MayaLindqvist = new(G("5e11e700", 2), "Maya Lindqvist", "maya.lindqvist@contososubsea.example", "ML");

    /// <summary>A new seller who exists in CRM but not yet as a BC salesperson (scene 7a).</summary>
    public static readonly Seller RobinHale = new(G("5e11e700", 99), "Robin Hale", "robin.hale@contososubsea.example", null);

    /// <summary>All sellers. Every seller except <see cref="RobinHale"/> has a BC salesperson code.</summary>
    public static readonly IReadOnlyList<Seller> Sellers =
    [
        AlexRivera,
        MayaLindqvist,
        new(G("5e11e700", 3), "Oliver Grant", "oliver.grant@contososubsea.example", "OG"),
        new(G("5e11e700", 4), "Priya Shah", "priya.shah@contososubsea.example", "PS"),
        new(G("5e11e700", 5), "Jonas Berg", "jonas.berg@contososubsea.example", "JB"),
        new(G("5e11e700", 6), "Chloe Martin", "chloe.martin@contososubsea.example", "CM"),
        new(G("5e11e700", 7), "Samuel Okoye", "samuel.okoye@contososubsea.example", "SO"),
        new(G("5e11e700", 8), "Freya Nielsen", "freya.nielsen@contososubsea.example", "FN"),
        new(G("5e11e700", 9), "Lucas Moreau", "lucas.moreau@contososubsea.example", "LM"),
        new(G("5e11e700", 10), "Hana Sato", "hana.sato@contososubsea.example", "HS"),
        RobinHale,
    ];

    // ---- Items ---------------------------------------------------------------------------

    public static readonly IReadOnlyList<Item> Items =
    [
        new(G("17e30000", 1), "WNCH-E20", "Electric ROV winch, 20 kN", 184000m, "PCS", false),
        new(G("17e30000", 2), "LARS-AF5", "A-frame launch & recovery system, 5 t", 412000m, "PCS", false),
        new(G("17e30000", 3), "CONN-WM8", "Wet-mate connector, 8-pin", 2450m, "PCS", false),
        new(G("17e30000", 4), "FORJ-2CH", "Fibre-optic rotary joint, 2-channel", 18900m, "PCS", false),
        new(G("17e30000", 5), "CBL-TOW100", "Armoured tow cable, per 100 m", 9600m, "PCS", false),
        new(G("17e30000", 6), "TSP-100", "Towed sensor platform", 96500m, "PCS", false),
        new(G("17e30000", 7), "CAM-4K", "Subsea camera, 4K", 14200m, "PCS", false),
        new(G("17e30000", 8), "SRV-ANNUAL", "Service agreement, annual", 22000m, "YEAR", false),
        // Discontinued in BC but still in the CRM product list: the drift behind the optional
        // blocked-item scene.
        new(G("17e30000", 9), "CAM-HD", "Subsea camera, HD (discontinued)", 8900m, "PCS", true),
    ];

    // ---- Accounts ------------------------------------------------------------------------

    public static readonly Account Fabrikam = new(
        G("acc00000", 0x101), "Fabrikam Offshore Energy", "Kanalsletta 4", "Stavanger", "4033", "NO",
        "+47 51 00 10 00", "https://fabrikam-offshore.example", "NO912345678MVA", AlexRivera.SystemUserId,
        new(G("cc000000", 0x101), "Ingrid", "Solberg", "ingrid.solberg@fabrikam-offshore.example", "+47 51 00 10 01", "Head of Marine Operations"),
        new(G("bc000000", 0xc0010), "C00010", 250000m, 42350m, 0m, "30 DAYS", ""));

    public static readonly Account Northwind = new(
        G("acc00000", 0x102), "Northwind Ocean Survey", "Harbour Road 7", "Plymouth", "PL1 3DE", "GB",
        "+44 1752 000 200", "https://northwind-survey.example", "GB987654321", MayaLindqvist.SystemUserId,
        new(G("cc000000", 0x102), "Tom", "Whitaker", "tom.whitaker@northwind-survey.example", "+44 1752 000 201", "Survey Manager"),
        new(G("bc000000", 0xc0020), "C00020", 150000m, 0m, 0m, "14 DAYS", ""));

    public static readonly Account Litware = new(
        G("acc00000", 0x103), "Litware Renewables", "Am Sandtorkai 20", "Hamburg", "20457", "DE",
        "+49 40 0000 300", "https://litware-renewables.example", "DE123456789", G("5e11e700", 3),
        new(G("cc000000", 0x103), "Katrin", "Vogel", "katrin.vogel@litware-renewables.example", "+49 40 0000 301", "Procurement Lead"),
        new(G("bc000000", 0xc0030), "C00030", 400000m, 118900m, 12400m, "60 DAYS", ""));

    public static readonly Account Adatum = new(
        G("acc00000", 0x104), "Adatum Hydrographic", "Water Street 1100", "Halifax", "B3H 1A1", "CA",
        "+1 902 000 0400", "https://adatum-hydro.example", "CA123456789RT0001", G("5e11e700", 4),
        new(G("cc000000", 0x104), "Luc", "Tremblay", "luc.tremblay@adatum-hydro.example", "+1 902 000 0401", "Fleet Engineer"),
        new(G("bc000000", 0xc0040), "C00040", 100000m, 9800m, 0m, "30 DAYS", ""));

    /// <summary>A CRM-only prospect owned by the seller who is missing in BC (scene 7a).</summary>
    public static readonly Account TreyResearch = new(
        G("acc00000", 0x106), "Trey Research Vessels", "Marine Drive 300", "Seattle", "98121", "US",
        "+1 206 000 0600", "https://trey-research.example", "US-EIN-00-0000600", RobinHale.SystemUserId,
        new(G("cc000000", 0x106), "Dana", "Whitfield", "dana.whitfield@trey-research.example", "+1 206 000 0601", "Vessel Program Director"),
        null);

    /// <summary>A CRM-only prospect.</summary>
    public static readonly Account Proseware = new(
        G("acc00000", 0x107), "Proseware Cable Systems", "Wilhelminakade 100", "Rotterdam", "3072 AP", "NL",
        "+31 10 000 0700", "https://proseware-cables.example", "NL123456789B01", MayaLindqvist.SystemUserId,
        new(G("cc000000", 0x107), "Sanne", "de Vries", "sanne.devries@proseware-cables.example", "+31 10 000 0701", "Project Buyer"),
        null);

    /// <summary>A throwaway prospect for the pre-demo warm-up cycle; the talk track never opens it.</summary>
    public static readonly Account WarmUp = new(
        G("acc00000", 0x199), "Wingtip Marine (warm-up)", "Test Street 1", "Oslo", "0150", "NO",
        "+47 22 00 00 00", "https://wingtip-marine.example", "NO999999999MVA", AlexRivera.SystemUserId,
        new(G("cc000000", 0x199), "Warm", "Up", "warm.up@wingtip-marine.example", "+47 22 00 00 01", "Test contact"),
        null);

    public static readonly IReadOnlyList<Account> Accounts = [Fabrikam, Northwind, Litware, Adatum, TreyResearch, Proseware, WarmUp];

    // ---- Opportunities ---------------------------------------------------------------------

    public static readonly IReadOnlyList<Opportunity> Opportunities =
    [
        new(G("0bb00000", 0x101), "OPP-10011", "Survey vessel winch replacement", Fabrikam.AccountId, AlexRivera.SystemUserId,
            Stages.Develop, 45, 50, [("WNCH-E20", 1m), ("CBL-TOW100", 2m)], null),
        new(G("0bb00000", 0x102), "OPP-10012", "Camera and connector refresh", Northwind.AccountId, MayaLindqvist.SystemUserId,
            Stages.Develop, 30, 40, [("CAM-4K", 4m), ("CONN-WM8", 16m)], null),
        new(G("0bb00000", 0x103), "OPP-10013", "Cable and connector framework 2027", Litware.AccountId, G("5e11e700", 3),
            Stages.Propose, 20, 70, [("CBL-TOW100", 20m), ("CONN-WM8", 24m)],
            new SentQuote(G("b0e00000", 0x1001), "S-QUO1001", SentDaysAgo: 3, ValidForDays: 30)),
        new(G("0bb00000", 0x106), "OPP-10016", "Launch and recovery system for new research vessel", TreyResearch.AccountId, RobinHale.SystemUserId,
            Stages.Develop, 60, 40, [("LARS-AF5", 1m), ("WNCH-E20", 1m)], null),
        new(G("0bb00000", 0x107), "OPP-10017", "Tow cable for survey spread", Proseware.AccountId, MayaLindqvist.SystemUserId,
            Stages.Develop, 25, 50, [("CBL-TOW100", 8m), ("FORJ-2CH", 1m)], null),
        new(G("0bb00000", 0x199), "OPP-10099", "Warm-up opportunity", WarmUp.AccountId, AlexRivera.SystemUserId,
            Stages.Develop, 10, 10, [("CONN-WM8", 1m)], null),
    ];

    // ---- Leads -----------------------------------------------------------------------------

    /// <summary>The lead scene 1 qualifies.</summary>
    public static readonly Lead Tailspin = new(
        G("1ead0000", 1), "ROV winch upgrade for research vessel", "Tailspin Marine Research", "Hannah", "Okafor",
        "hannah.okafor@tailspin-marine.example", "+44 23 8000 1001", "Chief Scientist", "Ocean Way 12", "Southampton",
        "SO14 3ZH", "GB", "https://tailspin-marine.example", "GB123456789", 210000m, AlexRivera.SystemUserId,
        QualifiedAccountId: G("acc00000", 0x105), QualifiedContactId: G("cc000000", 0x105),
        QualifiedOpportunityId: G("0bb00000", 0x105), QualifiedOpportunityNumber: "OPP-10025");

    public static readonly IReadOnlyList<Lead> Leads =
    [
        Tailspin,
        new(G("1ead0000", 2), "Towed sensor platform for survey season", "Relecloud Ocean Data", "Mateo", "Alvarez",
            "mateo.alvarez@relecloud-ocean.example", "+34 986 000 200", "Operations Manager", "Avenida del Puerto 5", "Vigo",
            "36202", "ES", "https://relecloud-ocean.example", "ESB12345678", 96500m, MayaLindqvist.SystemUserId,
            G("acc00000", 0x108), G("cc000000", 0x108), G("0bb00000", 0x108), "OPP-10028"),
        new(G("1ead0000", 3), "Connectors for offshore wind export cable", "City Power & Light", "Grace", "Lindberg",
            "grace.lindberg@citypower.example", "+46 31 000 300", "Offshore Wind Package Lead", "Hamngatan 3", "Gothenburg",
            "41104", "SE", "https://citypower.example", "SE556677889901", 60000m, G("5e11e700", 5),
            G("acc00000", 0x109), G("cc000000", 0x109), G("0bb00000", 0x109), "OPP-10029"),
    ];

    /// <summary>First number the D365 simulator uses for opportunities created later (bursts).</summary>
    public const int NextOpportunityNumber = 10101;

    /// <summary>Builds a readable, fixed GUID: <c>{prefix}-0000-4000-8000-{n:x12}</c>.</summary>
    private static Guid G(string prefix, long n) => Guid.Parse($"{prefix}-0000-4000-8000-{n:x12}");
}
