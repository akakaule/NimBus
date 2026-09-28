using System.Globalization;
using D365Sales.Api.Data;

namespace D365Sales.Api.Domain;

/// <summary>Adds timeline entries (the activity wall on accounts, opportunities and leads).</summary>
public static class Timeline
{
    public const string User = "User";
    public const string Integration = "Integration";
    public const string System = "System";

    public static void Add(D365DbContext db, Guid regardingId, string title, string? detail, string source, DateTimeOffset when) =>
        db.Timeline.Add(new TimelineEntry
        {
            Id = Guid.NewGuid(),
            RegardingId = regardingId,
            Title = title,
            Detail = detail,
            Source = source,
            CreatedOn = when,
        });

    /// <summary>Formats an amount the way the demo shows money: €204,600.</summary>
    public static string Money(decimal amount, string currencyCode = "EUR") =>
        (currencyCode == "EUR" ? "€" : currencyCode + " ") + amount.ToString("#,0", CultureInfo.InvariantCulture);
}
