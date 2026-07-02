using System.Collections.Generic;

namespace NimBus.WebApp.Services
{
    /// <summary>
    /// Tracks which event types an agent has subscribed to so that
    /// <c>/api/agent/receive</c> can filter parked events when no explicit
    /// <c>eventTypeId</c> query parameter is supplied (spec 022, Task 11).
    /// v1 is in-memory only; durable subscription persistence is out of scope.
    /// </summary>
    public interface IAgentSubscriptionRegistry
    {
        /// <summary>Records that <paramref name="agentId"/> is interested in <paramref name="eventTypeId"/>.</summary>
        void Subscribe(string agentId, string eventTypeId);

        /// <summary>Returns the event types <paramref name="agentId"/> has subscribed to (empty when none).</summary>
        IReadOnlyCollection<string> GetSubscriptions(string agentId);
    }
}
