using Microsoft.Extensions.Configuration;

namespace NimBus.WebApp.Services.IntegrationIntelligence;

/// <summary>
/// Non-secret settings captured at registration, never mutated by Admin saves or configuration reloads.
/// <paramref name="CredentialSource"/> reports where this instance's provider key came from ("saved", "deployment" or "none"), never its value.
/// </summary>
public sealed record IntelligenceSettingsSnapshot(IntelligenceAdminSettings Active, string Revision, bool LoadFailed, bool CredentialConfigured, string CredentialSource)
{
    public static IntelligenceSettingsSnapshot Create(IConfiguration configuration)
    {
        IntelligenceAdminSettings active;
        var failed = string.Equals(configuration[IntelligenceSettingsBootstrap.FailureKey], "true", StringComparison.OrdinalIgnoreCase);
        try { active = IntelligenceAdminSettings.FromConfiguration(configuration); }
        catch (Exception) { active = new(); failed = true; }
        var configured = IntelligenceSettingsBootstrap.HasDeploymentCredential(configuration);
        var source = configuration[IntelligenceSettingsBootstrap.ApiKeySourceKey] ?? (configured ? "deployment" : "none");
        return new(active, configuration[IntelligenceSettingsBootstrap.RevisionKey] ?? "none", failed, configured, source);
    }
}
