
namespace NimBus.WebApp.Constants
{
    public static class EventSignalNames
    {
        public const string GridUpdate = "gridupdate";
        public const string EndpointUpdate = "endpointupdate";

        /// <summary>
        /// An endpoint's heartbeat state changed — sent after a probe is written,
        /// swept, or settled by the Resolver.
        /// </summary>
        public const string HeartbeatUpdate = "heartbeatupdate";

        /// <summary>
        /// A platform service's liveness changed. Broadcast to all clients: the hub
        /// has no group for the Resolver, which is not an endpoint.
        /// </summary>
        public const string ServiceHealthUpdate = "servicehealthupdate";
    }

    public static class AppEndpoints
    {
        public const string GridEventHub = "/hubs/gridevents";
    }

    public static class TypeScriptOutputOptions
    {
        public const string OutputDir = "Client/src/tsd";
    }
}
