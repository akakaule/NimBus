using Microsoft.Extensions.Options;
using NimBus.Core;
using NimBus.MessageStore.Abstractions;
using NimBus.MessageStore.States;
using NimBus.WebApp.RateLimiting;

namespace NimBus.WebApp.Mcp.Access;

/// <summary>Outcome of saving the MCP access policy.</summary>
public enum McpAccessSaveStatus
{
    /// <summary>Saved.</summary>
    Saved,

    /// <summary>The policy is invalid; see the errors.</summary>
    Invalid,

    /// <summary>The change widens access and was not confirmed.</summary>
    ConfirmationRequired,

    /// <summary>Another save happened since the editor read the policy.</summary>
    Conflict,
}

/// <summary>Result of <see cref="McpAccessSettingsService.SaveAsync"/> and <see cref="McpAccessSettingsService.TurnOffAsync"/>.</summary>
/// <param name="Status">The outcome.</param>
/// <param name="Saved">The stored policy after the call.</param>
/// <param name="PreviousRevision">The revision that was replaced.</param>
/// <param name="Changes">What changed, or would have.</param>
/// <param name="Errors">Validation errors.</param>
public sealed record McpAccessSaveResult(
    McpAccessSaveStatus Status,
    McpAccessSettings Saved,
    string? PreviousRevision,
    IReadOnlyList<McpAccessChange> Changes,
    IReadOnlyList<string> Errors);

/// <summary>
/// Reads and saves the MCP access policy for the Admin API (Spec 037). A save on this instance
/// replaces its enforced policy at once; other instances pick it up within the cache TTL.
/// </summary>
public sealed class McpAccessSettingsService
{
    private const int TurnOffAttempts = 3;

    private readonly IEndpointMetadataStore _store;
    private readonly IPlatform _platform;
    private readonly RateLimitOptions _rateLimits;
    private readonly IMcpAccessPolicyProvider? _policies;
    private readonly TimeProvider _time;

    /// <summary>Creates the service.</summary>
    public McpAccessSettingsService(
        IEndpointMetadataStore store,
        IPlatform platform,
        IOptions<RateLimitOptions> rateLimits,
        TimeProvider time,
        IEnumerable<IMcpAccessPolicyProvider> policies)
    {
        ArgumentNullException.ThrowIfNull(rateLimits);
        _store = store;
        _platform = platform;
        _rateLimits = rateLimits.Value;
        _time = time;
        _policies = policies.FirstOrDefault();
    }

    /// <summary>The catalog's endpoint ids, in catalog order.</summary>
    public IReadOnlyList<string> CatalogEndpoints => _platform.Endpoints.Select(e => e.Id).ToList();

    /// <summary>The stored policy, or the defaults with a null revision.</summary>
    public Task<McpAccessSettings> GetAsync() => _store.GetMcpAccessSettings();

    /// <summary>
    /// Validates and saves <paramref name="next"/> when the stored revision is still
    /// <paramref name="expectedRevision"/>. A change that widens access needs
    /// <paramref name="confirmWidening"/>.
    /// </summary>
    public async Task<McpAccessSaveResult> SaveAsync(McpAccessSettings next, string? expectedRevision, bool confirmWidening, string? updatedBy)
    {
        ArgumentNullException.ThrowIfNull(next);
        var current = await _store.GetMcpAccessSettings().ConfigureAwait(false);

        var errors = McpAccessRules.Normalize(next, CatalogEndpoints, _rateLimits);
        if (errors.Count > 0)
            return new McpAccessSaveResult(McpAccessSaveStatus.Invalid, current, current.Revision, [], errors);

        if (!string.Equals(current.Revision, expectedRevision, StringComparison.Ordinal))
            return new McpAccessSaveResult(McpAccessSaveStatus.Conflict, current, current.Revision, [], []);

        var changes = McpAccessRules.Changes(current, next);
        if (!confirmWidening && changes.Any(c => c.Widens))
            return new McpAccessSaveResult(McpAccessSaveStatus.ConfirmationRequired, current, current.Revision, changes, []);

        Stamp(next, updatedBy);
        if (!await _store.TrySetMcpAccessSettings(next, current.Revision).ConfigureAwait(false))
            return new McpAccessSaveResult(McpAccessSaveStatus.Conflict, await _store.GetMcpAccessSettings().ConfigureAwait(false), current.Revision, [], []);

        _policies?.Accept(next);
        return new McpAccessSaveResult(McpAccessSaveStatus.Saved, next, current.Revision, changes, []);
    }

    /// <summary>
    /// Turns the endpoint off on the latest stored policy, whatever revision the caller last
    /// read, keeping every other setting. Retries a lost race a few times.
    /// </summary>
    public async Task<McpAccessSaveResult> TurnOffAsync(string? updatedBy)
    {
        for (var attempt = 0; ; attempt++)
        {
            var current = await _store.GetMcpAccessSettings().ConfigureAwait(false);
            var next = current.Clone();
            next.Enabled = false;
            Stamp(next, updatedBy);

            if (await _store.TrySetMcpAccessSettings(next, current.Revision).ConfigureAwait(false))
            {
                _policies?.Accept(next);
                return new McpAccessSaveResult(McpAccessSaveStatus.Saved, next, current.Revision, McpAccessRules.Changes(current, next), []);
            }

            if (attempt + 1 >= TurnOffAttempts)
                return new McpAccessSaveResult(McpAccessSaveStatus.Conflict, current, current.Revision, [], []);
        }
    }

    private void Stamp(McpAccessSettings settings, string? updatedBy)
    {
        settings.Id = McpAccessSettings.SingletonId;
        settings.Revision = Guid.NewGuid().ToString();
        settings.UpdatedBy = updatedBy;
        settings.UpdatedAtUtc = _time.GetUtcNow().UtcDateTime;
    }
}
