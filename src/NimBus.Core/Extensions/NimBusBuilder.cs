using Microsoft.Extensions.DependencyInjection;
using NimBus.Core.Messages;
using System;
using System.Collections.Generic;
using System.Linq;

namespace NimBus.Core.Extensions
{
    /// <summary>
    /// Default implementation of <see cref="INimBusBuilder"/>.
    /// </summary>
    public class NimBusBuilder : INimBusBuilder
    {
        private readonly List<Type> _pipelineBehaviorTypes = [];
        private readonly List<Type> _lifecycleObserverTypes = [];

        public NimBusBuilder(IServiceCollection services)
        {
            Services = services ?? throw new ArgumentNullException(nameof(services));
        }

        public IServiceCollection Services { get; }

        public INimBusBuilder AddPipelineBehavior<TBehavior>() where TBehavior : class, IMessagePipelineBehavior
        {
            _pipelineBehaviorTypes.Add(typeof(TBehavior));
            Services.AddSingleton<TBehavior>();
            return this;
        }

        public INimBusBuilder AddLifecycleObserver<TObserver>() where TObserver : class, IMessageLifecycleObserver
        {
            _lifecycleObserverTypes.Add(typeof(TObserver));
            Services.AddSingleton<IMessageLifecycleObserver, TObserver>();
            return this;
        }

        public INimBusBuilder AddExtension<TExtension>() where TExtension : class, INimBusExtension, new()
        {
            var extension = new TExtension();
            extension.Configure(this);
            return this;
        }

        public INimBusBuilder AddExtension(INimBusExtension extension)
        {
            if (extension == null) throw new ArgumentNullException(nameof(extension));
            extension.Configure(this);
            return this;
        }

        /// <summary>
        /// Explicit opt-out for hosts that compose only the pipeline / publishing /
        /// subscribing surface and never read or write the NimBus message store
        /// (e.g. pure adapter processes that own their own outbox via
        /// <c>AddNimBusSqlServerOutbox</c> and don't host the Resolver).
        /// Without this, builder validation requires <see cref="StorageProviderRegistrationTypeName"/>.
        /// </summary>
        public INimBusBuilder WithoutStorageProvider()
        {
            Services.AddSingleton<NoStorageProviderMarker>();
            return this;
        }

        // Internal marker used by Build() to recognise the explicit opt-out.
        private sealed class NoStorageProviderMarker { }

        /// <summary>
        /// Builds the pipeline and registers remaining infrastructure services.
        /// Called internally by <see cref="NimBusServiceCollectionExtensions.AddNimBus"/>.
        /// </summary>
        internal void Build()
        {
            // Register the lifecycle notifier (aggregates all observers)
            Services.AddSingleton<MessageLifecycleNotifier>();

            // Register the pipeline behavior types list for the pipeline to resolve
            Services.AddSingleton(new PipelineBehaviorRegistry(_pipelineBehaviorTypes));

            // Register the message pipeline
            Services.AddSingleton<MessagePipeline>();

            ValidateStorageProvider();
        }

        // Looked up by string to avoid a project reference from NimBus.Core to
        // NimBus.MessageStore.Abstractions (which already references Core).
        private const string StorageProviderRegistrationTypeName =
            "NimBus.MessageStore.Abstractions.IStorageProviderRegistration";

        private void ValidateStorageProvider()
        {
            var hasOptOut = Services.Any(sd => sd.ServiceType == typeof(NoStorageProviderMarker));

            var providerDescriptors = Services
                .Where(sd => string.Equals(sd.ServiceType.FullName, StorageProviderRegistrationTypeName, StringComparison.Ordinal))
                .ToList();

            if (hasOptOut && providerDescriptors.Count > 0)
            {
                throw new InvalidOperationException(
                    "WithoutStorageProvider() was called alongside an AddXxxMessageStore() registration. " +
                    "Pick one: either explicitly opt out of storage, or register exactly one provider.");
            }

            if (hasOptOut)
            {
                return;
            }

            if (providerDescriptors.Count == 0)
            {
                throw new InvalidOperationException(
                    "No NimBus storage provider has been registered. Call AddCosmosDbMessageStore() or " +
                    "AddSqlServerMessageStore() (or another provider's registration extension) inside your " +
                    "AddNimBus(builder => ...) configuration. Pure publisher/subscriber adapters that don't " +
                    "use the message store can call WithoutStorageProvider() instead.");
            }

            if (providerDescriptors.Count > 1)
            {
                throw new InvalidOperationException(
                    $"More than one NimBus storage provider was registered ({providerDescriptors.Count}). " +
                    "Exactly one provider must be active per running application instance. Remove all but one of " +
                    "the AddXxxMessageStore() calls in your AddNimBus(builder => ...) configuration.");
            }
        }
    }

    /// <summary>
    /// Registry of pipeline behavior types, used to resolve behaviors in order.
    /// </summary>
    public class PipelineBehaviorRegistry
    {
        public IReadOnlyList<Type> BehaviorTypes { get; }

        public PipelineBehaviorRegistry(IEnumerable<Type> behaviorTypes)
        {
            BehaviorTypes = behaviorTypes?.ToList().AsReadOnly()
                ?? throw new ArgumentNullException(nameof(behaviorTypes));
        }
    }
}
