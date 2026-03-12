using Microsoft.Extensions.DependencyInjection;
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
            builder.Services.AddSingleton<IManagerClient, ManagerClient>();
            return builder;
        }
    }
}
