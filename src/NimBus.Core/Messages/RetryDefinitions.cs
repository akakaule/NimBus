using System;
using System.Collections.Generic;

namespace NimBus.Core.Messages
{
    /// <summary>
    /// Legacy hardcoded retry definitions. Use <see cref="IRetryPolicyProvider"/> and
    /// <see cref="DefaultRetryPolicyProvider"/> for configurable retry policies.
    /// </summary>
    [Obsolete("Use IRetryPolicyProvider and DefaultRetryPolicyProvider for configurable retry policies.")]
    public class RetryDefinitions : IRetryPolicyProvider
    {
        private static RetryDefinitions Instance;
        public Dictionary<string, RetryDefinition> definitionDict { get; set; }

        private RetryDefinitions()
        {
            definitionDict = new Dictionary<string, RetryDefinition>();

            definitionDict.Add("AliceSaidHelloWithRetry", new RetryDefinition() { RetryCount = 1, RetryDelay = 1 });
            definitionDict.Add("CustomerMDRequestProcessed", new RetryDefinition() { RetryCount = 15, RetryDelay = 1 });
        }

        private static RetryDefinitions GetInstance()
        {
            if (Instance == null)
            {
                Instance = new RetryDefinitions();
            }
            return Instance;
        }

        public static RetryDefinition GetRetryDefinition(string eventTypeId, string message, string endpoint = null)
        {
            var definition = GetDefinitionByExceptionMessage(message, eventTypeId);
            if (definition != null)
            {
                return definition;
            }

            return GetDefinitionByEventTypeId(eventTypeId);
        }

        private static RetryDefinition GetDefinitionByExceptionMessage(string message, string eventTypeId)
        {
            if ((eventTypeId.Equals("BankFWFileReady") ||
               eventTypeId.Equals("BankStatementFileReady") ||
               eventTypeId.Equals("BIFileReady") ||
               eventTypeId.Equals("GeneralLedgerFileReady") ||
               eventTypeId.Equals("GRMFileReady") ||
               eventTypeId.Equals("RefinitivSpotRatesReady"))
               && message.Contains("The operation was canceled"))
            {
                return new RetryDefinition() { RetryCount = 2, RetryDelay = 10 };
            }

            return null;
        }

        private static RetryDefinition GetDefinitionByEventTypeId(string eventTypeId)
        {
            if (string.IsNullOrEmpty(eventTypeId))
                return null;

            GetInstance().definitionDict.TryGetValue(eventTypeId, out var definition);
            return definition;
        }

        /// <summary>
        /// IRetryPolicyProvider implementation that bridges legacy RetryDefinition to RetryPolicy.
        /// </summary>
        RetryPolicy IRetryPolicyProvider.GetRetryPolicy(string eventTypeId, string exceptionMessage, string endpoint)
        {
            var definition = GetRetryDefinition(eventTypeId, exceptionMessage, endpoint);
            if (definition == null)
                return null;

            return new RetryPolicy
            {
                MaxRetries = definition.RetryCount,
                Strategy = BackoffStrategy.Fixed,
                BaseDelay = TimeSpan.FromMinutes(definition.RetryDelay)
            };
        }
    }

    [Obsolete("Use RetryPolicy instead.")]
    public class RetryDefinition
    {
        public int RetryCount { get; set; }
        public int RetryDelay { get; set; }
    }
}
