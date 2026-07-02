using System.Security.Cryptography;
using System.Text;

namespace NimBus.MessageStore.States;

/// <summary>Stable fingerprint of a JSON-Schema string, for mapping drift detection (spec 023).</summary>
public static class SchemaHash
{
    /// <summary>
    /// Returns the SHA-256 hex digest of the UTF-8 encoded <paramref name="jsonSchema"/> string.
    /// Provides a stable fingerprint so the Mapping Executor can detect when a source schema
    /// has changed since the mapping was authored.
    /// </summary>
    public static string Of(string jsonSchema)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(jsonSchema ?? string.Empty));
        return Convert.ToHexString(bytes);
    }
}
