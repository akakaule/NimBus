using Microsoft.Extensions.DependencyInjection;
using NimBus.Core.Events;
using NimBus.Core.Messages;
using NimBus.SDK.EventHandlers;
using System;
using System.Collections.Generic;

namespace NimBus.SDK.Extensions
{
    /// <summary>
    /// Builder for configuring NimBus subscriber handlers and retry policies via DI.
    /// </summary>
    public class NimBusSubscriberBuilder
    {
        internal readonly IServiceCollection Services;
        internal readonly List<HandlerRegistration> HandlerRegistrations = new();
        internal Action<DefaultRetryPolicyProvider> RetryPolicyConfigurator;

        public NimBusSubscriberBuilder(IServiceCollection services)
        {
            Services = services ?? throw new ArgumentNullException(nameof(services));
        }

        /// <summary>
        /// Registers an event handler. The handler is resolved from DI per message.
        /// </summary>
        /// <typeparam name="TEvent">The event type.</typeparam>
        /// <typeparam name="THandler">The handler implementation type.</typeparam>
        public NimBusSubscriberBuilder AddHandler<TEvent, THandler>()
            where TEvent : IEvent
            where THandler : class, IEventHandler<TEvent>
        {
            Services.AddTransient<IEventHandler<TEvent>, THandler>();

            HandlerRegistrations.Add(new HandlerRegistration
            {
                EventTypeId = new EventType<TEvent>().Id,
                EventType = typeof(TEvent),
                Register = (provider, handlerProvider) =>
                {
                    handlerProvider.RegisterHandler<TEvent>(() =>
                    {
                        return provider.GetRequiredService<IEventHandler<TEvent>>();
                    });
                }
            });

            return this;
        }

        /// <summary>
        /// Configures retry policies for this subscriber.
        /// </summary>
        public NimBusSubscriberBuilder ConfigureRetryPolicies(Action<DefaultRetryPolicyProvider> configure)
        {
            RetryPolicyConfigurator = configure ?? throw new ArgumentNullException(nameof(configure));
            return this;
        }

        /// <summary>
        /// Configures the permanent failure classifier. Exceptions classified as permanent
        /// are dead-lettered immediately without consuming retry budget.
        /// </summary>
        public NimBusSubscriberBuilder ConfigurePermanentFailureClassifier(Action<DefaultPermanentFailureClassifier> configure)
        {
            var classifier = new DefaultPermanentFailureClassifier();
            configure(classifier);
            Services.AddSingleton<IPermanentFailureClassifier>(classifier);
            return this;
        }

        internal class HandlerRegistration
        {
            public string EventTypeId { get; set; }
            public Type EventType { get; set; }
            public Action<IServiceProvider, EventHandlerProvider> Register { get; set; }
        }
    }
}
