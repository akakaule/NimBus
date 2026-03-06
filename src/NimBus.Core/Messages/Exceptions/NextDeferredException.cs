using System;

namespace NimBus.Core.Messages
{
    public class NextDeferredException : Exception
    {
        public NextDeferredException(string message) : base(message)
        {
        }
    }
}