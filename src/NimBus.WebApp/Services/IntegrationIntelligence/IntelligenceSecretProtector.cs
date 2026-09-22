using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace NimBus.WebApp.Services.IntegrationIntelligence;

/// <summary>Seals and unseals the administrator-saved provider API key. Values never leave the process in clear text.</summary>
public interface IIntelligenceSecretProtector
{
    /// <summary>Returns an opaque payload safe to persist in the shared settings record.</summary>
    string Protect(string secret);

    /// <summary>Returns the clear-text secret, or null when the payload is empty, corrupt or sealed by a different key ring.</summary>
    string? TryUnprotect(string? protectedSecret);
}

/// <summary>Shared constants so the pre-host bootstrap and the running WebApp resolve the same key ring and purpose.</summary>
public static class IntelligenceSecretProtector
{
    /// <summary>Data Protection application discriminator; identical in Startup and the settings bootstrap.</summary>
    public const string ApplicationName = "NimBus.WebApp";

    /// <summary>Purpose string; changing it makes every saved key unreadable, so treat it as a schema version.</summary>
    public const string Purpose = "NimBus.IntegrationIntelligence.AdminSettings.ProviderApiKey.v1";
}

/// <summary>ASP.NET Data Protection implementation. Keys live in the host key ring, never in the settings store.</summary>
public sealed class DataProtectionSecretProtector(IDataProtectionProvider provider) : IIntelligenceSecretProtector
{
    private readonly IDataProtector _protector = provider.CreateProtector(IntelligenceSecretProtector.Purpose);

    public string Protect(string secret) => _protector.Protect(secret);

    public string? TryUnprotect(string? protectedSecret)
    {
        if (string.IsNullOrWhiteSpace(protectedSecret)) return null;
        try
        {
            var value = _protector.Unprotect(protectedSecret);
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch (CryptographicException) { return null; }
        catch (FormatException) { return null; }
    }
}
