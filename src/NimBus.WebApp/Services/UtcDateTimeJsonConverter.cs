using System;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NimBus.WebApp.Services;

/// <summary>
/// Forces every <see cref="DateTime"/> in the API response to be emitted as
/// UTC with a <c>Z</c> suffix. NimBus's domain treats every persisted
/// timestamp as UTC by convention (<c>EnqueuedTimeUtc</c>, <c>UpdatedAt</c>,
/// <c>AuditTimestamp</c>, etc.), but storage round-trips through Cosmos / SQL
/// often return values with <see cref="DateTimeKind.Unspecified"/>, which
/// System.Text.Json then serialises *without* a <c>Z</c> suffix
/// (<c>"2026-05-22T18:19:51.066"</c>). The browser's moment.js then parses
/// such a string as local time, so a user in CEST sees the UTC wall-clock
/// rendered as their local time — exactly 2 hours behind the truth.
///
/// <para>This converter normalises on the way out: <c>Unspecified</c> is
/// re-stamped as UTC (the wall-clock is preserved); <c>Local</c> is shifted
/// to UTC; <c>Utc</c> passes through. Read uses the default reader, which
/// already parses <c>Z</c>-suffixed input into <see cref="DateTimeKind.Utc"/>.</para>
/// </summary>
internal sealed class UtcDateTimeJsonConverter : JsonConverter<DateTime>
{
    private const string IsoUtcFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";

    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.GetDateTime();

    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
    {
        var utc = value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            // Unspecified → treat the wall-clock AS UTC. This is the case the
            // converter exists to fix; any other interpretation would silently
            // shift timestamps that are actually UTC by the server's local offset.
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        };

        writer.WriteStringValue(utc.ToString(IsoUtcFormat, CultureInfo.InvariantCulture));
    }
}

/// <summary>
/// Companion to <see cref="UtcDateTimeJsonConverter"/> for nullable
/// <see cref="DateTime"/> properties. Required because System.Text.Json
/// converter lookup doesn't unwrap nullables automatically.
/// </summary>
internal sealed class NullableUtcDateTimeJsonConverter : JsonConverter<DateTime?>
{
    private static readonly UtcDateTimeJsonConverter Inner = new();

    public override DateTime? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.Null ? null : reader.GetDateTime();

    public override void Write(Utf8JsonWriter writer, DateTime? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        Inner.Write(writer, value.Value, options);
    }
}
