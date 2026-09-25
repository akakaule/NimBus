namespace NimBus.WebApp.Mcp;

/// <summary>How callers of the operator MCP endpoint are authenticated.</summary>
public enum McpAuthenticationMode
{
    /// <summary>The endpoint is not registered.</summary>
    Disabled,

    /// <summary>
    /// Local Aspire development: the existing local-dev bypass authenticates every caller as
    /// the local developer. No sign-in; loopback callers only.
    /// </summary>
    LocalDevelopment,

    /// <summary>Entra bearer tokens issued for the MCP resource's app registration.</summary>
    Entra,
}

/// <summary>Chooses the <see cref="McpAuthenticationMode"/> once, at startup.</summary>
public static class McpAuthenticationModeResolver
{
    /// <summary>
    /// Resolves the mode. The local-dev bypass wins whenever it is active, matching the
    /// WebApp's own authentication ladder. An enabled endpoint with neither the bypass nor
    /// an Entra registration fails startup rather than running unauthenticated.
    /// </summary>
    /// <param name="options">The bound <c>NimBus:Mcp</c> options.</param>
    /// <param name="isDevelopment">Whether the host environment is Development.</param>
    /// <param name="localDevAuthenticationEnabled">The <c>EnableLocalDevAuthentication</c> setting.</param>
    /// <exception cref="InvalidOperationException">Enabled without any usable authentication.</exception>
    public static McpAuthenticationMode Resolve(
        McpOperatorOptions options,
        bool isDevelopment,
        bool localDevAuthenticationEnabled)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!options.Enabled)
            return McpAuthenticationMode.Disabled;

        if (isDevelopment && localDevAuthenticationEnabled)
            return McpAuthenticationMode.LocalDevelopment;

        if (options.Entra.IsConfigured)
            return McpAuthenticationMode.Entra;

        throw new InvalidOperationException(
            "NimBus:Mcp:Enabled is true, but no authentication is available for the MCP endpoint. " +
            "Set NimBus:Mcp:Entra:TenantId and NimBus:Mcp:Entra:ClientId to the MCP resource's Entra app registration, " +
            "or run in Development with EnableLocalDevAuthentication=true.");
    }
}
