using System;
using System.Buffers.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NimBus.MessageStore;

namespace NimBus.WebApp.Services.Operations;

/// <summary>
/// The state an operator command was decided against: the row's status, session, latest
/// attempt and update timestamp. A command runs only while the row still has exactly this
/// version, which is what the store's guarded archive checks.
/// </summary>
/// <param name="Status">Resolution status of the row.</param>
/// <param name="SessionId">Session the row belongs to.</param>
/// <param name="LastMessageId">The row's latest message, the attempt being acted on.</param>
/// <param name="UpdatedAtTicks">The row's update timestamp, in ticks.</param>
public sealed record OperatorMessageVersion(
    ResolutionStatus Status,
    string? SessionId,
    string? LastMessageId,
    long UpdatedAtTicks)
{
    /// <summary>The row's update timestamp, as the store compares it.</summary>
    [JsonIgnore]
    public DateTime UpdatedAt => new(UpdatedAtTicks, DateTimeKind.Utc);

    /// <summary>The version of <paramref name="row"/>.</summary>
    public static OperatorMessageVersion From(UnresolvedEvent row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return new(row.ResolutionStatus, row.SessionId, row.LastMessageId, row.UpdatedAt.Ticks);
    }

    /// <summary>An opaque, URL-safe form for clients to hand back unchanged.</summary>
    public string Encode() =>
        Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(this));

    /// <summary>Parses <see cref="Encode"/> output; false for anything else.</summary>
    public static bool TryDecode(string? value, out OperatorMessageVersion? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        try
        {
            version = JsonSerializer.Deserialize<OperatorMessageVersion>(Base64Url.DecodeFromChars(value));
            return version != null && Enum.IsDefined(version.Status);
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            version = null;
            return false;
        }
    }

    /// <summary>Whether <paramref name="row"/> still has this version.</summary>
    public bool Matches(UnresolvedEvent row) =>
        row.ResolutionStatus == Status
        && string.Equals(row.SessionId, SessionId, StringComparison.Ordinal)
        && string.Equals(row.LastMessageId, LastMessageId, StringComparison.Ordinal)
        && row.UpdatedAt.Ticks == UpdatedAtTicks;

    /// <inheritdoc/>
    public override string ToString() => Encode();
}
