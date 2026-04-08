using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Reflection;

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

        public virtual string GetSessionId()
        {
            var attr = GetType().GetCustomAttribute<SessionKeyAttribute>();
            if (attr is null)
                return Guid.NewGuid().ToString();

            var prop = GetType().GetProperty(attr.PropertyName);
            if (prop is null)
                throw new InvalidOperationException(
                    $"[SessionKey(\"{attr.PropertyName}\")] on {GetType().Name} references a property that does not exist.");

            return prop.GetValue(this)?.ToString() ?? Guid.NewGuid().ToString();
        }
    }
}
