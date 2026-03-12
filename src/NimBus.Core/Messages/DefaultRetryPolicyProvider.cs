using System;
using System.Collections.Generic;

namespace NimBus.Core.Messages
{
    /// <summary>
    /// Configurable retry policy provider. Supports per-event-type policies,
    /// exception-based policies, and a default fallback policy.
    /// </summary>
    public class DefaultRetryPolicyProvider : IRetryPolicyProvider
    {
        private readonly Dictionary<string, RetryPolicy> _eventTypePolicies = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<ExceptionRetryRule> _exceptionRules = new();
        private RetryPolicy _defaultPolicy;

        /// <summary>
        /// Sets a retry policy for a specific event type.
        /// </summary>
        public DefaultRetryPolicyProvider AddEventTypePolicy(string eventTypeId, RetryPolicy policy)
        {
            _eventTypePolicies[eventTypeId] = policy ?? throw new ArgumentNullException(nameof(policy));
            return this;
        }

        /// <summary>
        /// Adds a retry rule that matches when the exception message contains the specified text.
        /// Optionally scoped to specific event types.
        /// </summary>
        public DefaultRetryPolicyProvider AddExceptionRule(string exceptionContains, RetryPolicy policy, params string[] eventTypeIds)
        {
            _exceptionRules.Add(new ExceptionRetryRule
            {
                ExceptionContains = exceptionContains ?? throw new ArgumentNullException(nameof(exceptionContains)),
                Policy = policy ?? throw new ArgumentNullException(nameof(policy)),
                EventTypeIds = eventTypeIds?.Length > 0 ? new HashSet<string>(eventTypeIds, StringComparer.OrdinalIgnoreCase) : null
            });
            return this;
        }

        /// <summary>
        /// Sets a default retry policy used when no specific policy matches.
        /// </summary>
        public DefaultRetryPolicyProvider SetDefaultPolicy(RetryPolicy policy)
        {
            _defaultPolicy = policy;
            return this;
        }

        public RetryPolicy GetRetryPolicy(string eventTypeId, string exceptionMessage, string endpoint = null)
        {
            // 1. Check exception-based rules first (most specific)
            foreach (var rule in _exceptionRules)
            {
                if (rule.EventTypeIds != null && !rule.EventTypeIds.Contains(eventTypeId))
                    continue;

                if (!string.IsNullOrEmpty(exceptionMessage) && exceptionMessage.Contains(rule.ExceptionContains, StringComparison.OrdinalIgnoreCase))
                    return rule.Policy;
            }

            // 2. Check event-type-specific policies
            if (!string.IsNullOrEmpty(eventTypeId) && _eventTypePolicies.TryGetValue(eventTypeId, out var policy))
                return policy;

            // 3. Fall back to default policy
            return _defaultPolicy;
        }

        private class ExceptionRetryRule
        {
            public string ExceptionContains { get; set; }
            public RetryPolicy Policy { get; set; }
            public HashSet<string> EventTypeIds { get; set; }
        }
    }
}
