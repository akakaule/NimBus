using System;

namespace NimBus.MessageStore
{
    public class RequestLimitException : Exception
    {
        public TimeSpan? RetryAfter { get; }

        public RequestLimitException()
            : base("Cosmos DB request limit exceeded")
        {
        }

        public RequestLimitException(TimeSpan? retryAfter)
            : base("Cosmos DB request limit exceeded")
        {
            RetryAfter = retryAfter;
        }

        public RequestLimitException(string message)
            : base(message)
        {
        }

        public RequestLimitException(string message, TimeSpan? retryAfter)
            : base(message)
        {
            RetryAfter = retryAfter;
        }

        public RequestLimitException(string message, Exception inner)
            : base(message, inner)
        {
        }

        public RequestLimitException(string message, Exception inner, TimeSpan? retryAfter)
            : base(message, inner)
        {
            RetryAfter = retryAfter;
        }
    }
}
