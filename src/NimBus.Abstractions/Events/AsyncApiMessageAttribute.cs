using System;

namespace NimBus.Core.Events
{
    /// <summary>
    /// Enriches the AsyncAPI message that <c>nb catalog asyncapi</c> generates for an event
    /// contract. Optional: when absent, the exporter falls back to the type name and any
    /// <see cref="System.ComponentModel.DescriptionAttribute"/>. Apply to an event class:
    /// <code>[AsyncApiMessage(Title = "Customer created", Summary = "Published when a customer is created", Tags = new[] { "CRM" })]</code>
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
    public sealed class AsyncApiMessageAttribute : Attribute
    {
        /// <summary>Human-friendly message title (AsyncAPI <c>message.title</c>). Defaults to the type name.</summary>
        public string Title { get; set; }

        /// <summary>Short one-line summary (AsyncAPI <c>message.summary</c>). Defaults to the <c>[Description]</c>.</summary>
        public string Summary { get; set; }

        /// <summary>Longer description (AsyncAPI <c>message.description</c>).</summary>
        public string Description { get; set; }

        /// <summary>Governance tags (AsyncAPI <c>message.tags</c>), e.g. owning team or business capability.</summary>
        public string[] Tags { get; set; }
    }
}
