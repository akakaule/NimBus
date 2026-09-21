using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace NimBus.Extensions.IntegrationIntelligence.Evidence;

/// <summary>Removes common credential-shaped values from provider evidence.</summary>
public sealed class IntelligenceDataRedactor
{
    private readonly HashSet<string> _additionalSecretKeys;

    /// <summary>Creates a redactor with optional operator-defined sensitive keys.</summary>
    public IntelligenceDataRedactor(FailureClassificationOptions? options = null)
        => _additionalSecretKeys = (options?.Data.AdditionalRedactedKeys ?? [])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> SecretKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "password", "secret", "clientSecret", "token", "accessToken", "refreshToken",
        "authorization", "apiKey", "connectionString", "cookie", "bearer", "sharedAccessKey",
    };

    private static readonly Regex SecretPattern = new(
        "(?i)(Authorization\\s*:\\s*Bearer\\s*|SharedAccessKey\\s*[=:]\\s*|Password\\s*[=:]\\s*|AccountKey\\s*[=:]\\s*)([^\\s,;]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    private static readonly Regex ExceptionDumpPattern = new(
        "(?m)^\\s*at\\s+[^\\r\\n]+$|--- End of (?:inner exception|stack trace)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    /// <summary>Returns a scrubbed free-text field, or null for embedded exception dumps.</summary>
    public static string? ScrubText(string? value, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value) || ExceptionDumpPattern.IsMatch(value))
        {
            return null;
        }

        var scrubbed = SecretPattern.Replace(value, "$1[redacted]");
        return scrubbed.Length <= maximumLength ? scrubbed : scrubbed[..maximumLength];
    }

    /// <summary>Scrubs secret-shaped object values while preserving JSON structure.</summary>
    public string? ScrubJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var token = JToken.Parse(json);
            ScrubToken(token);
            return token.ToString(Newtonsoft.Json.Formatting.None);
        }
        catch (Newtonsoft.Json.JsonException)
        {
            return null;
        }
    }

    private void ScrubToken(JToken token)
    {
        if (token is JObject obj)
        {
            foreach (var property in obj.Properties().ToList())
            {
                if (SecretKeys.Contains(property.Name) || _additionalSecretKeys.Contains(property.Name))
                {
                    property.Value = "[redacted]";
                }
                else
                {
                    ScrubToken(property.Value);
                }
            }
        }
        else if (token is JArray array)
        {
            foreach (var child in array) ScrubToken(child);
        }
    }
}
