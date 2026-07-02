using System;
using System.Collections.Generic;
using System.Collections.Concurrent;

namespace NimBus.WebApp.Services
{
    /// <summary>
    /// Thread-safe, in-memory <see cref="IAgentSubscriptionRegistry"/>. Registered as a
    /// singleton so subscriptions survive across requests for the process lifetime.
    /// Subscriptions are NOT persisted (spec 022 v1 scope).
    /// </summary>
    public sealed class AgentSubscriptionRegistry : IAgentSubscriptionRegistry
    {
        // agentId -> set of eventTypeId. The inner dictionary is used as a concurrent set.
        private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _byAgent =
            new(StringComparer.Ordinal);

        public void Subscribe(string agentId, string eventTypeId)
        {
            if (string.IsNullOrWhiteSpace(agentId) || string.IsNullOrWhiteSpace(eventTypeId))
                return;

            var set = _byAgent.GetOrAdd(agentId, _ => new ConcurrentDictionary<string, byte>(StringComparer.Ordinal));
            set[eventTypeId] = 0;
        }

        public IReadOnlyCollection<string> GetSubscriptions(string agentId)
        {
            if (!string.IsNullOrWhiteSpace(agentId) && _byAgent.TryGetValue(agentId, out var set))
                return new List<string>(set.Keys);

            return Array.Empty<string>();
        }
    }
}
