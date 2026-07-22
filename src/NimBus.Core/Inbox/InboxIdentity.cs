using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace NimBus.Core.Inbox;

/// <summary>
/// Computes the canonical, byte-exact deduplication identity for an (endpoint, message) pair.
/// </summary>
/// <remarks>
/// Providers must never compare the raw identifier strings with storage-native string
/// semantics: SQL Server, for example, pads NVARCHAR operands with trailing spaces for
/// equality and unique-key comparisons regardless of collation, which would conflate the
/// distinct message ids <c>"m1"</c> and <c>"m1 "</c>. Hashing a delimited, length-prefixed
/// encoding gives every provider the same exact-match key: the endpoint length followed by
/// <c>':'</c> makes the encoding unambiguous for any identifier content (including endpoint
/// ids that start with digits), so distinct pairs never produce the same input.
/// </remarks>
public static class InboxIdentity
{
    // U+001F (unit separator) — cannot appear inside the decimal length prefix, so the
    // encoding parses unambiguously for any endpoint/message content.
    private const char Delimiter = (char)0x1F;

    /// <summary>
    /// Computes the SHA-256 identity hash for the supplied endpoint and message identifiers.
    /// </summary>
    /// <param name="endpointId">The consuming endpoint identity.</param>
    /// <param name="messageId">The broker message identifier.</param>
    /// <returns>The 32-byte identity hash.</returns>
    public static byte[] ComputeHash(string endpointId, string messageId)
    {
        ArgumentNullException.ThrowIfNull(endpointId);
        ArgumentNullException.ThrowIfNull(messageId);

        var identity = string.Create(
            CultureInfo.InvariantCulture,
            $"{endpointId.Length}{Delimiter}{endpointId}{Delimiter}{messageId}");
        return SHA256.HashData(Encoding.UTF8.GetBytes(identity));
    }
}
