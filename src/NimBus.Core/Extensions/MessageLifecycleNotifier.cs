using NimBus.Core.Messages;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NimBus.Core.Extensions
{
    /// <summary>
    /// Aggregates all registered <see cref="IMessageLifecycleObserver"/> instances
    /// and broadcasts lifecycle events to them.
    /// </summary>
    public class MessageLifecycleNotifier
    {
        private readonly IReadOnlyList<IMessageLifecycleObserver> _observers;

        public MessageLifecycleNotifier(IEnumerable<IMessageLifecycleObserver> observers)
        {
            _observers = (observers ?? []).ToList().AsReadOnly();
        }

        public bool HasObservers => _observers.Count > 0;

        public async Task NotifyReceived(IMessageContext context, CancellationToken cancellationToken = default)
        {
            if (!HasObservers) return;
            var lifecycleContext = MessageLifecycleContext.FromMessageContext(context);
            foreach (var observer in _observers)
            {
                await observer.OnMessageReceived(lifecycleContext, cancellationToken);
            }
        }

        public async Task NotifyCompleted(IMessageContext context, CancellationToken cancellationToken = default)
        {
            if (!HasObservers) return;
            var lifecycleContext = MessageLifecycleContext.FromMessageContext(context);
            foreach (var observer in _observers)
            {
                await observer.OnMessageCompleted(lifecycleContext, cancellationToken);
            }
        }

        public async Task NotifyFailed(IMessageContext context, Exception exception, CancellationToken cancellationToken = default)
        {
            if (!HasObservers) return;
            var lifecycleContext = MessageLifecycleContext.FromMessageContext(context);
            foreach (var observer in _observers)
            {
                await observer.OnMessageFailed(lifecycleContext, exception, cancellationToken);
            }
        }

        public async Task NotifyDeadLettered(IMessageContext context, string reason, Exception exception = null, CancellationToken cancellationToken = default)
        {
            if (!HasObservers) return;
            var lifecycleContext = MessageLifecycleContext.FromMessageContext(context);
            foreach (var observer in _observers)
            {
                await observer.OnMessageDeadLettered(lifecycleContext, reason, exception, cancellationToken);
            }
        }
    }
}
