using Microsoft.Extensions.DependencyInjection;

namespace NimBus.Core.Extensions
{
    /// <summary>
    /// Defines a NimBus extension that can register services and configure the message pipeline.
    /// Implement this interface to create a NimBus extension package.
    /// </summary>
    public interface INimBusExtension
    {
        /// <summary>
        /// Configures the extension by registering services and pipeline behaviors.
        /// </summary>
        void Configure(INimBusBuilder builder);
    }
}
