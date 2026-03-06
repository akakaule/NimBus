using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace NimBus.Core.Events
{
    public interface IEvent
    {
        /// <summary>
        /// Unique id for the session that this event is part of.
        /// Ordered delivery are ensured within each session.
        /// Use SessionId to achieve ordered delivery when event depends on previous events.
        /// By default, each Event is assigned a unique SessionId, but this can be overridden if needed.
        /// Best practice is to keep the scope of the ordered delivery as tight as possible, and better yet to avoid it entirely whenever possible.
        /// </summary>
        string GetSessionId();

        /// <summary>
        /// Validate event. Throws if invalid
        /// </summary>
        void Validate();

        /// <summary>
        /// Validate the event and return result
        /// </summary>
        /// <returns>A tuple containing the validity of the event as well as a list of validation results</returns>
        EventValidationResult TryValidate();

        /// <summary>
        /// Event type information.
        /// </summary>
        IEventType GetEventType();
    }

    public class EventValidationResult
    {
        public EventValidationResult(bool isValid, IReadOnlyList<ValidationResult> validationResults)
        {
            IsValid = isValid;
            ValidationResults = validationResults;
        }

        public bool IsValid { get; }
        public IReadOnlyList<ValidationResult> ValidationResults { get; }
    }
}
