using Microsoft.Extensions.DependencyInjection;

namespace NimBus.Core.Extensions
{
    /// <summary>
    /// Builder for composing NimBus core services and extensions.
    /// </summary>
    public interface INimBusBuilder
    {
        /// <summary>
        /// The underlying service collection for registering dependencies.
        /// </summary>
        IServiceCollection Services { get; }

        /// <summary>
        /// Registers a message pipeline behavior (middleware) that wraps message handling.
        /// Behaviors execute in the order they are registered.
        /// </summary>
        INimBusBuilder AddPipelineBehavior<TBehavior>() where TBehavior : class, IMessagePipelineBehavior;

        /// <summary>
        /// Registers a message lifecycle observer that receives notifications about message events.
        /// </summary>
        INimBusBuilder AddLifecycleObserver<TObserver>() where TObserver : class, IMessageLifecycleObserver;

        /// <summary>
        /// Registers a NimBus extension.
        /// </summary>
        INimBusBuilder AddExtension<TExtension>() where TExtension : class, INimBusExtension, new();

        /// <summary>
        /// Registers a NimBus extension instance.
        /// </summary>
        INimBusBuilder AddExtension(INimBusExtension extension);
    }
}
