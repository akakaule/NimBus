using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace NimBus.Core.Events
{
    public abstract class Event : IEvent
    {
        public EventValidationResult TryValidate()
        {
            var validationResults = new List<ValidationResult>();
            return new EventValidationResult(Validator.TryValidateObject(this, new ValidationContext(this), validationResults, true), validationResults);
        }

        public void Validate()
        {
            Validator.ValidateObject(this, new ValidationContext(this), true);
        }

        public IEventType GetEventType() => new EventType(GetType());

        public virtual string GetSessionId() => Guid.NewGuid().ToString();
    }
}
