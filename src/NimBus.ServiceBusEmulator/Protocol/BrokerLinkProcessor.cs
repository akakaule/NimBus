using System.Runtime.CompilerServices;
using Amqp;
using Amqp.Framing;
using Amqp.Listener;
using Amqp.Types;
using NimBus.ServiceBusEmulator.Broker;

namespace NimBus.ServiceBusEmulator.Protocol;

internal sealed class BrokerLinkProcessor(
    BrokerNamespace broker,
    SessionLinkRegistry sessionLinks,
    int maxMessageSize = 262_144) : ILinkProcessor
{
    private static readonly Symbol SessionFilter = new("com.microsoft:session-filter");
    private static readonly Symbol LockedUntilUtc = new("com.microsoft:locked-until-utc");
    private static readonly Symbol TimeoutProperty = new("com.microsoft:timeout");

    public void Process(AttachContext context)
    {
        context.Attach.MaxMessageSize = (ulong)maxMessageSize;
        var rawAddress = context.Attach.Role
            ? (context.Attach.Source as Source)?.Address
            : (context.Attach.Target as Target)?.Address;
        var address = rawAddress?.TrimStart('/');
        EmulatorDiagnostics.Write(context.Attach.Role ? "Receive attach" : "Send attach", address);
        if (string.IsNullOrWhiteSpace(address))
        {
            context.Complete(NotFound("The AMQP link address is empty."));
            return;
        }

        if (!context.Attach.Role)
        {
            AttachSender(context, address);
            return;
        }

        AttachReceiverAsync(context, address).Observe();
    }

    private void AttachSender(AttachContext context, string topicName)
    {
        if (!broker.TopicExists(topicName))
        {
            context.Complete(NotFound($"Topic '{topicName}' does not exist."));
            return;
        }

        if (!broker.CanSend(topicName))
        {
            context.Complete(NotAllowed($"Topic '{topicName}' is send-disabled."));
            return;
        }

        context.Complete(new TargetLinkEndpoint(new TopicMessageProcessor(broker, topicName, maxMessageSize), context.Link), 100);
    }

    private async Task AttachReceiverAsync(AttachContext context, string address)
    {
        if (!TryParseSubscription(address, out var topicName, out var subscriptionName) ||
            !broker.SubscriptionExists(topicName, subscriptionName))
        {
            context.Complete(NotFound($"Subscription '{address}' does not exist."));
            return;
        }
        if (!broker.CanReceive(topicName, subscriptionName))
        {
            context.Complete(NotAllowed($"Subscription '{address}' is not available for receive."));
            return;
        }

        var source = (Source)context.Attach.Source;
        var owner = Guid.NewGuid().ToString("N");
        object? filterValue = null;
        var isSessionReceiver = source.FilterSet?.TryGetValue(SessionFilter, out filterValue) == true;
        string? sessionId = null;
        if (isSessionReceiver)
        {
            var requestedSession = filterValue switch
            {
                string value => value,
                DescribedValue { Value: string value } => value,
                _ => null,
            };
            var timeout = GetTimeout(context.Attach);
            var started = TimeProvider.System.GetTimestamp();
            AcceptedSession? accepted;
            do
            {
                accepted = null;
                try
                {
                    accepted = requestedSession is null
                        ? broker.TryAcceptNextSession(topicName, subscriptionName, owner)
                        : broker.TryAcceptSession(topicName, subscriptionName, requestedSession, owner);
                }
                catch (SessionCannotBeLockedException exception)
                {
                    context.Complete(new Error("com.microsoft:session-cannot-be-locked")
                    {
                        Description = exception.Message,
                    });
                    return;
                }
                if (accepted is not null)
                {
                    sessionId = accepted.SessionId;
                    sessionLinks.Register(context.Link.Session.Connection, context.Attach.LinkName, owner);
                    source.FilterSet![SessionFilter] = sessionId;
                    context.Attach.Properties ??= new Fields();
                    context.Attach.Properties[LockedUntilUtc] = accepted.LockedUntil.UtcTicks;
                    break;
                }

                if (requestedSession is null && TimeProvider.System.GetElapsedTime(started) >= timeout)
                {
                    context.Complete(new Error("com.microsoft:timeout") { Description = "No unlocked session is available." });
                    return;
                }

                await Task.Delay(25).ConfigureAwait(false);
            }
            while (!context.Link.IsClosed);

            if (sessionId is null)
            {
                return;
            }
        }

        var messageSource = new SubscriptionMessageSource(
            broker,
            sessionLinks,
            context.Link.Session.Connection,
            context.Attach.LinkName,
            topicName,
            subscriptionName,
            sessionId,
            owner);
        context.Complete(new ReleasingSourceLinkEndpoint(messageSource, context.Link), 0);
    }

    private static TimeSpan GetTimeout(Attach attach)
    {
        if (attach.Properties?.TryGetValue(TimeoutProperty, out var value) == true)
        {
            return value switch
            {
                uint milliseconds => TimeSpan.FromMilliseconds(milliseconds),
                int milliseconds => TimeSpan.FromMilliseconds(milliseconds),
                _ => TimeSpan.FromSeconds(60),
            };
        }

        return TimeSpan.FromSeconds(60);
    }

    private static Error NotFound(string description) => new("amqp:not-found") { Description = description };

    private static Error NotAllowed(string description) => new("amqp:not-allowed") { Description = description };

    private static bool TryParseSubscription(string address, out string topicName, out string subscriptionName)
    {
        const string separator = "/Subscriptions/";
        var index = address.IndexOf(separator, StringComparison.OrdinalIgnoreCase);
        if (index <= 0 || index + separator.Length >= address.Length)
        {
            topicName = string.Empty;
            subscriptionName = string.Empty;
            return false;
        }

        topicName = address[..index];
        subscriptionName = address[(index + separator.Length)..];
        return true;
    }

    private sealed class TopicMessageProcessor(BrokerNamespace broker, string topicName, int maxMessageSize) : IMessageProcessor
    {
        public int Credit => 100;

        public void Process(MessageContext messageContext)
        {
            EmulatorDiagnostics.Write("Transfer", topicName);
            try
            {
                var messages = AmqpMessageConverter.FromTransfer(messageContext.Message);
                if (messages.Any(message => message.Body.Length > maxMessageSize))
                {
                    messageContext.Complete(new Error("amqp:link:message-size-exceeded")
                    {
                        Description = $"Message exceeds the configured {maxMessageSize}-byte limit.",
                    });
                    return;
                }

                foreach (var brokerMessage in messages)
                {
                    broker.Publish(topicName, brokerMessage);
                }
                messageContext.Complete();
            }
            catch (BrokerQuotaExceededException exception)
            {
                messageContext.Complete(new Error("amqp:resource-limit-exceeded") { Description = exception.Message });
            }
            catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException)
            {
                messageContext.Complete(new Error("amqp:not-allowed") { Description = exception.Message });
            }
        }
    }

    private sealed class SubscriptionMessageSource(
        BrokerNamespace broker,
        SessionLinkRegistry sessionLinks,
        Connection connection,
        string linkName,
        string topicName,
        string subscriptionName,
        string? sessionId,
        string owner) : IMessageSource
    {
        public async Task<ReceiveContext?> GetMessageAsync(ListenerLink link)
        {
            while (!link.IsClosed)
            {
                // Only acquire (and thereby lock) a message while the peer still
                // grants credit: the client rescinds credit the moment its receive
                // call returns, and a message locked after that would be sent
                // uncredited, dropped unsettled, and stranded until lock expiry --
                // for session subscriptions, until the session lock itself lapses.
                if (GetCredit(link) > 0)
                {
                    var delivery = broker.TryAcquire(topicName, subscriptionName, sessionId, owner);
                    if (delivery is not null)
                    {
                        return new ReceiveContext(link, AmqpMessageConverter.ToAmqp(delivery.Message))
                        {
                            UserToken = delivery.LockToken,
                        };
                    }
                }

                if (link.IsDraining)
                {
                    return null;
                }

                await Task.Delay(25).ConfigureAwait(false);
            }

            return null;
        }

        // ListenerLink.Credit is internal in AMQPNetLite.Core 2.5.1.
        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Credit")]
        private static extern uint GetCredit(ListenerLink link);

        public void DisposeMessage(ReceiveContext receiveContext, DispositionContext dispositionContext)
        {
            if (receiveContext.UserToken is not Guid lockToken)
            {
                dispositionContext.Complete(new Error("amqp:not-found") { Description = "The delivery lock token is missing." });
                return;
            }

            try
            {
                switch (dispositionContext.DeliveryState)
                {
                    case Accepted:
                        broker.Complete(topicName, subscriptionName, lockToken, owner);
                        break;
                    case Released:
                        broker.Release(topicName, subscriptionName, lockToken, owner);
                        break;
                    case Modified modified when modified.DeliveryFailed:
                        broker.Abandon(topicName, subscriptionName, lockToken, owner);
                        break;
                    case Rejected rejected:
                        broker.DeadLetter(topicName, subscriptionName, lockToken, owner, rejected.Error?.Condition, rejected.Error?.Description);
                        break;
                    default:
                        broker.Abandon(topicName, subscriptionName, lockToken, owner);
                        break;
                }

                dispositionContext.Complete();
            }
            catch (KeyNotFoundException exception)
            {
                var condition = sessionId is null
                    ? "com.microsoft:message-lock-lost"
                    : "com.microsoft:session-lock-lost";
                dispositionContext.Complete(new Error(condition) { Description = exception.Message });
            }
        }

        public void ReleaseSession()
        {
            if (sessionId is not null)
            {
                sessionLinks.Unregister(connection, linkName, owner);
                broker.ReleaseSession(topicName, subscriptionName, sessionId, owner);
            }
        }
    }

    private sealed class ReleasingSourceLinkEndpoint(SubscriptionMessageSource source, ListenerLink link)
        : SourceLinkEndpoint(source, link)
    {
        public override void OnLinkClosed(ListenerLink closedLink, Error error)
        {
            source.ReleaseSession();
            base.OnLinkClosed(closedLink, error);
        }
    }
}

internal static class TaskExtensions
{
    public static async void Observe(this Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // AMQP.Net Lite owns the link lifecycle; a failed asynchronous attach is completed by the processor.
        }
    }
}
