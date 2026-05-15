using Microsoft.Extensions.DependencyInjection;
using NimBus.Core.Events;
using NimBus.Core.Messages;
using NimBus.SDK.EventHandlers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

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
            AddHandlerRegistration(typeof(TEvent), typeof(THandler), explicitRegistration: true);

            return this;
        }

        /// <summary>
        /// Registers all concrete <see cref="IEventHandler{T}"/> implementations from
        /// the assembly containing <typeparamref name="TMarker"/>.
        /// </summary>
        public NimBusSubscriberBuilder AddHandlersFromAssemblyContaining<TMarker>()
        {
            return AddHandlersFromAssembly(typeof(TMarker).Assembly);
        }

        /// <summary>
        /// Registers all concrete <see cref="IEventHandler{T}"/> implementations from the specified assembly.
        /// </summary>
        public NimBusSubscriberBuilder AddHandlersFromAssembly(Assembly assembly)
        {
            if (assembly == null) throw new ArgumentNullException(nameof(assembly));

            foreach (var registration in DiscoverHandlerRegistrations(assembly))
            {
                AddHandlerRegistration(registration.EventType, registration.HandlerType, explicitRegistration: false);
            }

            return this;
        }

        /// <summary>
        /// Registers all concrete <see cref="IEventHandler{T}"/> implementations from the specified assemblies.
        /// </summary>
        public NimBusSubscriberBuilder AddHandlersFromAssemblies(params Assembly[] assemblies)
        {
            if (assemblies == null) throw new ArgumentNullException(nameof(assemblies));

            foreach (var assembly in assemblies)
            {
                AddHandlersFromAssembly(assembly);
            }

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
            (configure ?? throw new ArgumentNullException(nameof(configure)))(classifier);
            Services.AddSingleton<IPermanentFailureClassifier>(classifier);
            return this;
        }

        private void AddHandlerRegistration(Type eventType, Type handlerType, bool explicitRegistration)
        {
            if (eventType == null) throw new ArgumentNullException(nameof(eventType));
            if (handlerType == null) throw new ArgumentNullException(nameof(handlerType));

            var expectedHandlerInterface = typeof(IEventHandler<>).MakeGenericType(eventType);
            if (!typeof(IEvent).IsAssignableFrom(eventType))
                throw new ArgumentException($"Event type '{eventType.FullName}' must implement {nameof(IEvent)}.", nameof(eventType));
            if (!expectedHandlerInterface.IsAssignableFrom(handlerType))
                throw new ArgumentException(
                    $"Handler type '{handlerType.FullName}' must implement IEventHandler<{eventType.Name}>.",
                    nameof(handlerType));

            var existing = HandlerRegistrations.SingleOrDefault(r => r.EventType == eventType);
            if (existing != null)
            {
                if (explicitRegistration)
                {
                    HandlerRegistrations.Remove(existing);
                }
                else if (existing.IsExplicit || existing.HandlerType == handlerType)
                {
                    return;
                }
                else
                {
                    throw new InvalidOperationException(
                        $"Multiple handlers were discovered for event type '{eventType.FullName}': " +
                        $"'{existing.HandlerType.FullName}' and '{handlerType.FullName}'. " +
                        "Register one handler explicitly with AddHandler<TEvent,THandler>() to choose the handler.");
                }
            }

            Services.AddTransient(expectedHandlerInterface, handlerType);

            HandlerRegistrations.Add(new HandlerRegistration
            {
                EventTypeId = new EventType(eventType).Id,
                EventType = eventType,
                HandlerType = handlerType,
                IsExplicit = explicitRegistration,
                Register = (provider, handlerProvider) =>
                {
                    handlerProvider.RegisterHandler(eventType, () => provider.GetRequiredService(expectedHandlerInterface));
                }
            });
        }

        private static IEnumerable<(Type EventType, Type HandlerType)> DiscoverHandlerRegistrations(Assembly assembly)
        {
            return GetLoadableTypes(assembly)
                .Where(type => type is { IsClass: true, IsAbstract: false } && !type.ContainsGenericParameters)
                .SelectMany(handlerType => handlerType
                    .GetInterfaces()
                    .Where(IsEventHandlerInterface)
                    .Select(handlerInterface => (EventType: handlerInterface.GetGenericArguments()[0], HandlerType: handlerType)));
        }

        private static bool IsEventHandlerInterface(Type type)
        {
            return type.IsGenericType
                && type.GetGenericTypeDefinition() == typeof(IEventHandler<>)
                && typeof(IEvent).IsAssignableFrom(type.GetGenericArguments()[0]);
        }

        private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
        {
            try
            {
                return assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                return ex.Types.Where(type => type != null).Cast<Type>();
            }
        }

        internal class HandlerRegistration
        {
            public string EventTypeId { get; set; }
            public Type EventType { get; set; }
            public Type HandlerType { get; set; }
            public bool IsExplicit { get; set; }
            public Action<IServiceProvider, EventHandlerProvider> Register { get; set; }
        }
    }
}
