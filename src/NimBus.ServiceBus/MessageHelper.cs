using NimBus.Core.Messages;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace NimBus.ServiceBus
{
    public static class MessageHelper
    {
        public static Azure.Messaging.ServiceBus.ServiceBusMessage ToServiceBusMessage(IMessage message, int messageEnqueueDelay = 0)
        {
            var result = new Azure.Messaging.ServiceBus.ServiceBusMessage();
            result.ApplicationProperties[UserPropertyName.To.ToString()] = message.To;
            result.ApplicationProperties[UserPropertyName.MessageType.ToString()] = message.MessageType.ToString();
            result.ApplicationProperties[UserPropertyName.EventId.ToString()] = message.EventId;
            result.ApplicationProperties[UserPropertyName.OriginatingMessageId.ToString()] = message.OriginatingMessageId ?? Constants.Self;
            result.ApplicationProperties[UserPropertyName.ParentMessageId.ToString()] = message.ParentMessageId ?? Constants.Self;
            result.ApplicationProperties[UserPropertyName.RetryCount.ToString()] = message.RetryCount ?? 0;
            result.ApplicationProperties[UserPropertyName.OriginatingFrom.ToString()] = message.OriginatingFrom ?? Constants.Self;
            if (!string.IsNullOrEmpty(message.From))
                result.ApplicationProperties[UserPropertyName.From.ToString()] = message.From;
            result.ApplicationProperties[UserPropertyName.EventTypeId.ToString()] =  message.EventTypeId ?? message.MessageContent?.EventContent?.EventTypeId;

            // Add OriginalSessionId and DeferralSequence if present (for deferred messages)
            if (!string.IsNullOrEmpty(message.OriginalSessionId))
            {
                result.ApplicationProperties[UserPropertyName.OriginalSessionId.ToString()] = message.OriginalSessionId;
            }
            if (message.DeferralSequence.HasValue)
            {
                result.ApplicationProperties[UserPropertyName.DeferralSequence.ToString()] = message.DeferralSequence.Value;
            }

            // Per-message timings: only set on response messages produced by the
            // receive pipeline (subscriber → Resolver). Original publishes leave
            // these null so they're absent on the wire.
            if (message.QueueTimeMs.HasValue)
            {
                result.ApplicationProperties[UserPropertyName.QueueTimeMs.ToString()] = message.QueueTimeMs.Value;
            }
            if (message.ProcessingTimeMs.HasValue)
            {
                result.ApplicationProperties[UserPropertyName.ProcessingTimeMs.ToString()] = message.ProcessingTimeMs.Value;
            }

            // Dead-letter notification messages carry the SB dead-letter properties as
            // user properties so the Resolver can mirror them into the audit record and
            // classify the resolution status as DeadLettered.
            if (!string.IsNullOrEmpty(message.DeadLetterReason))
            {
                result.ApplicationProperties[UserPropertyName.DeadLetterReason.ToString()] = message.DeadLetterReason;
            }
            if (!string.IsNullOrEmpty(message.DeadLetterErrorDescription))
            {
                result.ApplicationProperties[UserPropertyName.DeadLetterErrorDescription.ToString()] = message.DeadLetterErrorDescription;
            }

            // PendingHandoff metadata: carried on the PendingHandoffResponse to the
            // Resolver so the audit row records reason / external job id / deadline.
            if (!string.IsNullOrEmpty(message.HandoffReason))
            {
                result.ApplicationProperties[UserPropertyName.HandoffReason.ToString()] = message.HandoffReason;
            }
            if (!string.IsNullOrEmpty(message.ExternalJobId))
            {
                result.ApplicationProperties[UserPropertyName.ExternalJobId.ToString()] = message.ExternalJobId;
            }
            if (message.ExpectedBy.HasValue)
            {
                result.ApplicationProperties[UserPropertyName.ExpectedBy.ToString()] =
                    message.ExpectedBy.Value.ToUniversalTime().ToString("o", System.Globalization.CultureInfo.InvariantCulture);
            }

            var diagnosticId = message.DiagnosticId ?? Activity.Current?.Id;
            if (!string.IsNullOrEmpty(diagnosticId))
                result.ApplicationProperties[NimBusDiagnostics.DiagnosticIdProperty] = diagnosticId;

            result.ScheduledEnqueueTime = DateTime.UtcNow.AddMinutes(messageEnqueueDelay);
            var messageContentSerialized = JsonConvert.SerializeObject(message.MessageContent);
            result.Body = new BinaryData(Encoding.UTF8.GetBytes(messageContentSerialized));
            if (!string.IsNullOrWhiteSpace(message.MessageId))
                result.MessageId = message.MessageId;

            result.SessionId = message.SessionId;
            result.CorrelationId = message.CorrelationId;

            if (!string.IsNullOrEmpty(message.ReplyTo))
                result.ReplyTo = message.ReplyTo;
            if (!string.IsNullOrEmpty(message.ReplyToSessionId))
                result.ReplyToSessionId = message.ReplyToSessionId;

            return result;
        }

        /// <summary>
        /// Creates a ServiceBus message for the session-enabled deferred subscription.
        /// The message is routed via To="Deferred" and uses SessionId for session affinity.
        /// OriginalSessionId and DeferralSequence are kept for backward compatibility and ordering.
        /// </summary>
        public static Azure.Messaging.ServiceBus.ServiceBusMessage CreateDeferredMessage(IMessage message, string originalSessionId, int deferralSequence)
        {
            var result = new Azure.Messaging.ServiceBus.ServiceBusMessage();
            result.ApplicationProperties[UserPropertyName.To.ToString()] = Constants.DeferredSubscriptionName;
            result.ApplicationProperties[UserPropertyName.MessageType.ToString()] = message.MessageType.ToString();
            result.ApplicationProperties[UserPropertyName.EventId.ToString()] = message.EventId;
            result.ApplicationProperties[UserPropertyName.OriginatingMessageId.ToString()] = message.OriginatingMessageId ?? Constants.Self;
            result.ApplicationProperties[UserPropertyName.ParentMessageId.ToString()] = message.ParentMessageId ?? Constants.Self;
            result.ApplicationProperties[UserPropertyName.RetryCount.ToString()] = message.RetryCount ?? 0;
            result.ApplicationProperties[UserPropertyName.OriginatingFrom.ToString()] = message.OriginatingFrom ?? Constants.Self;
            result.ApplicationProperties[UserPropertyName.EventTypeId.ToString()] = message.EventTypeId ?? message.MessageContent?.EventContent?.EventTypeId;
            result.ApplicationProperties[UserPropertyName.OriginalSessionId.ToString()] = originalSessionId;
            result.ApplicationProperties[UserPropertyName.DeferralSequence.ToString()] = deferralSequence;

            var diagnosticId = message.DiagnosticId ?? Activity.Current?.Id;
            if (!string.IsNullOrEmpty(diagnosticId))
                result.ApplicationProperties[NimBusDiagnostics.DiagnosticIdProperty] = diagnosticId;

            var messageContentSerialized = JsonConvert.SerializeObject(message.MessageContent);
            result.Body = new BinaryData(Encoding.UTF8.GetBytes(messageContentSerialized));
            if (!string.IsNullOrWhiteSpace(message.MessageId))
                result.MessageId = message.MessageId;

            result.CorrelationId = message.CorrelationId;
            result.SessionId = originalSessionId;  // Session-enabled deferred subscription
            return result;
        }
    }
}
