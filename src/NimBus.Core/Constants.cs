using Newtonsoft.Json;

namespace NimBus.Core.Messages
{
    public class Constants
    {
        public const string ResolverId = "Resolver";
        public const string ManagerId = "Manager";
        public const string ContinuationId = "Continuation";
        public const string Self = "self";
        public const string EventId = "Event";
        public const string RetryId = "Retry";
        public const string DeferredProcessorId = "DeferredProcessor";
        public const string DeferredSubscriptionName = "Deferred";

        /// <summary>
        /// Safe JSON serializer settings that explicitly disable type name handling.
        /// Use these settings for all deserialization of untrusted data.
        /// </summary>
        public static readonly JsonSerializerSettings SafeJsonSettings = CreateSafeJsonSettings();

        /// <summary>
        /// Creates an isolated safe serializer configuration for untrusted JSON.
        /// Callers should prefer this factory over sharing the mutable
        /// <see cref="SafeJsonSettings"/> instance.
        /// </summary>
        /// <returns>A new safe serializer settings instance.</returns>
        public static JsonSerializerSettings CreateSafeJsonSettings() => new JsonSerializerSettings
        {
            TypeNameHandling = TypeNameHandling.None,
            MaxDepth = 32
        };
    }
}
