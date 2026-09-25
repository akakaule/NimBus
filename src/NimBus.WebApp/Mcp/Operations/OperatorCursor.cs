using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NimBus.WebApp.Mcp.Operations;

/// <summary>
/// Opaque paging cursors bound to the query and caller that produced them. The cursor carries
/// the store's continuation token, but a cursor replayed with a different query or by another
/// caller is refused. It grants nothing: every page re-checks authorization.
/// </summary>
public static class OperatorCursor
{
    private sealed record Payload(string Scope, string Token);

    /// <summary>Wraps <paramref name="token"/>; returns null when there is no next page.</summary>
    /// <param name="token">The store's continuation token.</param>
    /// <param name="scope">A canonical description of the query and caller.</param>
    public static string? Encode(string? token, string scope)
    {
        if (string.IsNullOrEmpty(token))
            return null;

        var json = JsonSerializer.SerializeToUtf8Bytes(new Payload(Hash(scope), token));
        return Base64Url.EncodeToString(json);
    }

    /// <summary>
    /// Returns the continuation token inside <paramref name="cursor"/>, or null for a first page.
    /// Throws <see cref="OperatorToolErrors.InvalidCursor"/> when it was issued for another scope.
    /// </summary>
    public static string? Decode(string? cursor, string scope)
    {
        if (string.IsNullOrWhiteSpace(cursor))
            return null;

        Payload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<Payload>(Base64Url.DecodeFromChars(cursor));
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            throw OperatorToolErrors.InvalidCursor();
        }

        if (payload is null || !string.Equals(payload.Scope, Hash(scope), StringComparison.Ordinal))
            throw OperatorToolErrors.InvalidCursor();

        return payload.Token;
    }

    private static string Hash(string scope)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scope)), 0, 16);
}
