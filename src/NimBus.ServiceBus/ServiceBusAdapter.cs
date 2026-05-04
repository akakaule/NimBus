using Azure.Messaging.ServiceBus;
using NimBus.Core.Messages;
using NimBus.MessageStore.Abstractions;
using Microsoft.Azure.Functions.Worker;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Threading;
using System.Threading.Tasks;

namespace NimBus.ServiceBus
{
    public interface IServiceBusAdapter
    {
        Task Handle(ServiceBusReceivedMessage message, ServiceBusSessionMessageActions sessionActions, CancellationToken cancellationToken = default);
        Task Handle(ServiceBusReceivedMessage message, ServiceBusMessageActions messageActions, ServiceBusSessionMessageActions sessionActions, CancellationToken cancellationToken = default);
        Task Handle(ServiceBusReceivedMessage message, ServiceBusSessionReceiver sessionReceiver, CancellationToken cancellationToken = default);
        Task Handle(ProcessSessionMessageEventArgs args, CancellationToken cancellationToken = default);
    }

    public class ServiceBusAdapter : IServiceBusAdapter
    {
        private static readonly Meter s_meter = new("NimBus.ServiceBus");
        private static readonly Histogram<double> s_e2eLatency = s_meter.CreateHistogram<double>("nimbus.message.e2e_latency", "ms", "End-to-end message latency");
        private static readonly Histogram<double> s_queueWait = s_meter.CreateHistogram<double>("nimbus.message.queue_wait", "ms", "Queue wait time before processing");

        private readonly IMessageHandler _messageHandler;
        private readonly ServiceBusClient _serviceBusClient;
        private readonly string _entityPath;
        private readonly ISessionStateStore _sessionStateStore;

        /// <summary>
        /// Creates a new ServiceBusAdapter.
        /// </summary>
        /// <param name="messageHandler">The message handler to process messages.</param>
        /// <param name="serviceBusClient">Optional ServiceBusClient for receiving deferred messages in isolated worker model.
        /// Inject via dependency injection if you need to use ReceiveDeferredMessageAsync.</param>
        /// <param name="entityPath">Optional entity path (queue name or topic/subscription path) for receiving deferred messages.</param>
        /// <param name="sessionStateStore">Optional ISessionStateStore. When provided, the obsolete session-state bridges on
        /// IMessageContext (BlockSession, deferred-count helpers, …) delegate to it. When null, those bridges keep their
        /// legacy SB-session-state behaviour. Production hosts wire this from DI; unit tests can leave it null.</param>
        public ServiceBusAdapter(IMessageHandler messageHandler, ServiceBusClient serviceBusClient = null, string entityPath = null, ISessionStateStore sessionStateStore = null)
        {
            _messageHandler = messageHandler ?? throw new ArgumentNullException(nameof(messageHandler));
            _serviceBusClient = serviceBusClient;
            _entityPath = entityPath;
            _sessionStateStore = sessionStateStore;
        }

        public async Task Handle(ServiceBusReceivedMessage message, ServiceBusSessionMessageActions sessionActions, CancellationToken cancellationToken = default)
        {
            var messageWrapper = new ServiceBusMessage(message);
            var sessionWrapper = new ServiceBusSession(sessionActions, _serviceBusClient, _entityPath, message.SessionId);
            var messageContext = new MessageContext(messageWrapper, sessionWrapper, _sessionStateStore);

            await HandleWithLatencyTracking(message, messageContext, cancellationToken);
        }

        public async Task Handle(ServiceBusReceivedMessage message, ServiceBusMessageActions messageActions, ServiceBusSessionMessageActions sessionActions, CancellationToken cancellationToken = default)
        {
            var messageWrapper = new ServiceBusMessage(message);
            var sessionWrapper = new ServiceBusSession(messageActions, sessionActions, _serviceBusClient, _entityPath, message.SessionId);
            var messageContext = new MessageContext(messageWrapper, sessionWrapper, _sessionStateStore);

            await HandleWithLatencyTracking(message, messageContext, cancellationToken);
        }

        public async Task Handle(ServiceBusReceivedMessage message, ServiceBusSessionReceiver sessionReceiver, CancellationToken cancellationToken = default)
        {
            var messageWrapper = new ServiceBusMessage(message);
            var sessionWrapper = new ServiceBusSession(sessionReceiver);
            var messageContext = new MessageContext(messageWrapper, sessionWrapper, _sessionStateStore);

            await HandleWithLatencyTracking(message, messageContext, cancellationToken);
        }

        public async Task Handle(ProcessSessionMessageEventArgs args, CancellationToken cancellationToken = default)
        {
            var messageWrapper = new ServiceBusMessage(args.Message);
            var sessionWrapper = new ServiceBusSession(args, _serviceBusClient, _entityPath);
            var messageContext = new MessageContext(messageWrapper, sessionWrapper, _sessionStateStore);

            await HandleWithLatencyTracking(args.Message, messageContext, cancellationToken);
        }

        private async Task HandleWithLatencyTracking(ServiceBusReceivedMessage message, MessageContext messageContext, CancellationToken cancellationToken)
        {
            var eventType = message.ApplicationProperties.TryGetValue("EventTypeId", out var et) ? et?.ToString() ?? "unknown" : "unknown";
            var destination = message.ApplicationProperties.TryGetValue("To", out var to) ? to?.ToString() ?? "unknown" : "unknown";

            // Extract W3C trace context from incoming message
            ActivityContext parentContext = default;
            if (message.ApplicationProperties.TryGetValue(NimBusDiagnostics.DiagnosticIdProperty, out var diagnosticId)
                && diagnosticId is string traceParent)
            {
                ActivityContext.TryParse(traceParent, null, out parentContext);
            }

            using var activity = NimBusDiagnostics.Source.StartActivity(
                "NimBus.Process",
                ActivityKind.Consumer,
                parentContext);

            activity?.SetTag("messaging.system", "servicebus");
            activity?.SetTag("messaging.destination", destination);
            activity?.SetTag("messaging.event_type", eventType);
            activity?.SetTag("messaging.operation", "process");
            activity?.SetTag("messaging.message_id", message.MessageId);
            activity?.SetTag("messaging.session_id", message.SessionId);

            var queueWaitMs = Math.Max(0, (DateTime.UtcNow - message.EnqueuedTime.UtcDateTime).TotalMilliseconds);
            s_queueWait.Record(queueWaitMs,
                new KeyValuePair<string, object>("messaging.event_type", eventType),
                new KeyValuePair<string, object>("messaging.destination", destination));

            // Stash on the context so ResponseService can copy it onto the
            // outgoing response message and the Resolver can persist it.
            messageContext.QueueTimeMs = (long)queueWaitMs;
            // Marker for processing-time computation. The terminal handler sends
            // the response INSIDE the pipeline, so any "set ProcessingTimeMs in
            // middleware after await next()" approach happens after the response
            // already left. Reading "now − HandlerStartedAtUtc" at response-build
            // time gives the correct elapsed without that ordering issue.
            messageContext.HandlerStartedAtUtc = DateTime.UtcNow;

            try
            {
                await _messageHandler.Handle(messageContext, cancellationToken);
            }
            catch (Exception ex)
            {
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                throw;
            }
            finally
            {
                var e2eMs = Math.Max(0, (DateTime.UtcNow - message.EnqueuedTime.UtcDateTime).TotalMilliseconds);
                s_e2eLatency.Record(e2eMs,
                    new KeyValuePair<string, object>("messaging.event_type", eventType),
                    new KeyValuePair<string, object>("messaging.destination", destination));
            }
        }
    }
}
