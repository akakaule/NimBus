using System;
using System.Collections.Generic;
using System.Text;
using System.ComponentModel.DataAnnotations;
using NimBus.Core.Events;

namespace NimBus.Validation
{
    class EventValidation
    {
        internal static void Validate(Event eventObject)
        {
            try
            {
                Validator.ValidateObject(eventObject, new ValidationContext(eventObject), true);
            }
            catch (System.ComponentModel.DataAnnotations.ValidationException e)
            {
                throw new InvalidEventException(eventObject.GetType().Name, e);
            }
        }
    }
}
