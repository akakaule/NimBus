namespace BusinessCentral.Api.Domain;

/// <summary>A BC customer template (Customer Templ.): the defaults a converted prospect gets.</summary>
public sealed record CustomerTemplate(string Code, string Description, decimal CreditLimit, string PaymentTermsCode);

/// <summary>The customer templates the simulator offers in the Make Order dialog.</summary>
public static class CustomerTemplates
{
    public static readonly CustomerTemplate Business = new("BUSINESS", "Business customer, EU", 100000m, "30 DAYS");
    public static readonly CustomerTemplate Export = new("EXPORT", "Business customer, outside the EU", 75000m, "60 DAYS");

    public static readonly IReadOnlyList<CustomerTemplate> All = [Business, Export];

    private static readonly HashSet<string> EuCountries = new(StringComparer.OrdinalIgnoreCase)
    {
        "AT", "BE", "BG", "HR", "CY", "CZ", "DK", "EE", "FI", "FR", "DE", "GR", "HU", "IE", "IT", "LV",
        "LT", "LU", "MT", "NL", "PL", "PT", "RO", "SK", "SI", "ES", "SE",
    };

    /// <summary>The template BC proposes for a prospect in <paramref name="countryCode"/>.</summary>
    public static CustomerTemplate DefaultFor(string? countryCode) =>
        countryCode is not null && EuCountries.Contains(countryCode) ? Business : Export;

    public static CustomerTemplate? Find(string? code) =>
        All.FirstOrDefault(t => string.Equals(t.Code, code, StringComparison.OrdinalIgnoreCase));
}
