using NimBus.Core.Messages;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
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

            result.ScheduledEnqueueTime = DateTime.UtcNow.AddMinutes(messageEnqueueDelay);
            var messageContentSerialized = JsonConvert.SerializeObject(message.MessageContent);
            result.Body = new BinaryData(Encoding.UTF8.GetBytes(messageContentSerialized));
            if (!string.IsNullOrWhiteSpace(message.MessageId))
                result.MessageId = message.MessageId;

            result.SessionId = message.SessionId;
            result.CorrelationId = message.CorrelationId;
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
