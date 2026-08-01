using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NimBus.Core.Extensions;

namespace NimBus.Manager
{
    /// <summary>
    /// Extension methods to register Manager services via the NimBus builder.
    /// </summary>
    public static class ManagerBuilderExtensions
    {
        /// <summary>
        /// Adds the Manager client for message resubmission and skip operations.
        /// Requires a ServiceBusClient to be registered.
        /// </summary>
        public static INimBusBuilder AddManager(this INimBusBuilder builder)
        {
            // Explicit factory so constructor selection never becomes ambiguous
            // between the MEL constructor and the obsolete Serilog bridge.
            builder.Services.AddSingleton<IManagerClient>(sp => new ManagerClient(
                sp.GetRequiredService<ServiceBusClient>(),
                sp.GetService<ILogger<ManagerClient>>()));
            return builder;
        }
    }
}
