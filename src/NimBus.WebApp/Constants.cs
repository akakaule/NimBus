
namespace NimBus.WebApp.Constants
{
    public static class EventSignalNames
    {
        public const string GridUpdate = "gridupdate";
        public const string EndpointUpdate = "endpointupdate";
        public const string HeartbeatUpdate = "heartbeatupdate";
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
