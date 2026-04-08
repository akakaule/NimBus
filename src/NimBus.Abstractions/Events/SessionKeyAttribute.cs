using System;

namespace NimBus.Core.Events
{
    /// <summary>
    /// Specifies which property determines the session ID for ordered delivery.
    /// Use with <c>nameof()</c> for compile-time safety:
    /// <code>[SessionKey(nameof(OrderId))]</code>
    /// This is an alternative to overriding <see cref="Event.GetSessionId"/>.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, Inherited = true, AllowMultiple = false)]
    public sealed class SessionKeyAttribute : Attribute
    {
        public string PropertyName { get; }

        public SessionKeyAttribute(string propertyName)
        {
            PropertyName = propertyName ?? throw new ArgumentNullException(nameof(propertyName));
        }
    }
}
