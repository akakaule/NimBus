using NimBus.Core;
using NimBus.WebApp.Services;

namespace NimBus.WebApp.Mcp.Operations;

/// <summary>
/// The catalog endpoints the current caller may read, resolved once per request. Every tool
/// that takes an endpoint id checks it here first, so an unreadable endpoint is refused before
/// any store is touched and ids are normalized to their catalog spelling.
/// </summary>
public sealed class OperatorEndpointCatalog
{
    private readonly IPlatform _platform;
    private readonly IEndpointAuthorizationService _authorization;
    private IReadOnlyList<string>? _readable;

    /// <summary>Creates the catalog for one request.</summary>
    public OperatorEndpointCatalog(IPlatform platform, IEndpointAuthorizationService authorization, IConfiguration configuration)
    {
        _platform = platform;
        _authorization = authorization;
        Environment = configuration.GetValue<string>("Environment");
    }

    /// <summary>The configured NimBus environment name.</summary>
    public string? Environment { get; }

    /// <summary>Listed endpoints the caller holds Reader on, in catalog order.</summary>
    public async Task<IReadOnlyList<string>> GetReadableEndpointIdsAsync()
    {
        if (_readable is not null)
            return _readable;

        var readable = new List<string>();
        foreach (var endpoint in _platform.Endpoints)
        {
            if (EndpointVisibility.IsListed(endpoint.Id, Environment)
                && await _authorization.HasRoleAsync(AccessRole.Reader, endpoint.Id).ConfigureAwait(false))
            {
                readable.Add(endpoint.Id);
            }
        }

        return _readable = readable;
    }

    /// <summary>
    /// Returns the catalog spelling of <paramref name="endpointId"/>, or throws
    /// <see cref="OperatorToolErrors.EndpointNotFound"/> when the caller cannot read it.
    /// </summary>
    public async Task<string> RequireReadableAsync(string endpointId)
    {
        if (string.IsNullOrWhiteSpace(endpointId))
            throw OperatorToolErrors.InvalidArgument("endpointId is required.");

        var readable = await GetReadableEndpointIdsAsync().ConfigureAwait(false);
        return readable.FirstOrDefault(id => string.Equals(id, endpointId.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? throw OperatorToolErrors.EndpointNotFound(endpointId);
    }
}
