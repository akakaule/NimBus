using Microsoft.Extensions.Configuration;

namespace NimBus.WebApp.Services
{
    /// <summary>
    /// Single source of truth for the Agent Zone endpoint id (spec 022). Both the
    /// publisher (Task 10) and the receive/settle controller (Task 11) must target the
    /// same endpoint container; keeping the config key and default in one place avoids
    /// magic-string drift between them.
    /// </summary>
    public static class AgentZone
    {
        /// <summary>Config key holding the Agent Zone endpoint id.</summary>
        public const string ConfigKey = "Agent:ZoneEndpointId";

        /// <summary>Fallback endpoint id used when <see cref="ConfigKey"/> is unset.</summary>
        public const string DefaultAgentZoneEndpointId = "AgentZoneEndpoint";

        /// <summary>Resolves the Agent Zone endpoint id from configuration, falling back to the default.</summary>
        public static string ResolveEndpointId(IConfiguration config)
            => config?[ConfigKey] ?? DefaultAgentZoneEndpointId;
    }
}
