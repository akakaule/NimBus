namespace NimBus.WebApp.Services;

/// <summary>
/// Which catalog endpoints appear in endpoint listings. The demo endpoints (Alice, Bob,
/// Charlie) are listed only in the dev environments. Shared by the REST endpoint lists and
/// the operator MCP tools so both show the same set.
/// </summary>
public static class EndpointVisibility
{
    private static readonly string[] DemoEndpoints = ["Alice", "Bob", "Charlie"];
    private static readonly string[] DemoEnvironments = ["dev", "sbdev"];

    /// <summary>
    /// True when <paramref name="endpointId"/> is listed in <paramref name="environment"/>
    /// (the configured <c>Environment</c> value, which may be absent).
    /// </summary>
    public static bool IsListed(string endpointId, string? environment)
    {
        if (DemoEnvironments.Contains(environment, StringComparer.OrdinalIgnoreCase))
            return true;

        return !DemoEndpoints.Contains(endpointId, StringComparer.OrdinalIgnoreCase);
    }
}
