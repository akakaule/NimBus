using System.Collections.Generic;

namespace NimBus.Core.Events
{
    /// <summary>
    /// Mutable AsyncAPI enrichment for a single event contract, set fluently through
    /// <c>AddNimBusPublisher(endpoint, b =&gt; b.Publish&lt;T&gt;(o =&gt; o.AsyncApi…))</c>. Mirrors
    /// <see cref="AsyncApiMessageAttribute"/>; when both are supplied for the same event the fluent
    /// value wins for scalars, tags and examples are unioned, and <see cref="Deprecated"/> is OR-ed.
    /// </summary>
    public sealed class AsyncApiMessageOptions
    {
        /// <summary>Custom message/schema name → AsyncAPI <c>message.name</c> and payload schema <c>title</c>.</summary>
        public string? Name { get; set; }

        /// <summary>Display title → AsyncAPI <c>message.title</c>.</summary>
        public string? Title { get; set; }

        /// <summary>One-line summary → AsyncAPI <c>message.summary</c>.</summary>
        public string? Summary { get; set; }

        /// <summary>Longer description → AsyncAPI <c>message.description</c>.</summary>
        public string? Description { get; set; }

        /// <summary>Owning individual/role → <c>x-nimbus-governance.owner</c>.</summary>
        public string? Owner { get; set; }

        /// <summary>Owning team → <c>x-nimbus-governance.team</c>.</summary>
        public string? Team { get; set; }

        /// <summary>Business capability → <c>x-nimbus-governance.businessCapability</c>.</summary>
        public string? BusinessCapability { get; set; }

        /// <summary>Contract version → <c>x-nimbus-governance.version</c>.</summary>
        public string? Version { get; set; }

        /// <summary>External documentation URL → AsyncAPI <c>message.externalDocs.url</c>.</summary>
        public string? ExternalDocsUrl { get; set; }

        /// <summary>External documentation description → AsyncAPI <c>message.externalDocs.description</c>.</summary>
        public string? ExternalDocsDescription { get; set; }

        /// <summary>
        /// Marks the contract deprecated → JSON-Schema/AsyncAPI <c>deprecated</c> marker on the payload
        /// schema, mirrored as <c>x-nimbus-governance.deprecated</c> on the message.
        /// </summary>
        public bool Deprecated { get; set; }

        /// <summary>Governance tags → AsyncAPI <c>message.tags</c>. Unioned with attribute tags.</summary>
        public IList<string> Tags { get; } = new List<string>();

        /// <summary>Payload examples → AsyncAPI <c>message.examples</c>. Appended after the derived sample.</summary>
        public IList<AsyncApiMessageExample> Examples { get; } = new List<AsyncApiMessageExample>();
    }

    /// <summary>A named payload example for an AsyncAPI message (<c>message.examples[]</c>).</summary>
    public sealed class AsyncApiMessageExample
    {
        /// <summary>Example name (<c>examples[].name</c>).</summary>
        public string? Name { get; set; }

        /// <summary>Example summary (<c>examples[].summary</c>).</summary>
        public string? Summary { get; set; }

        /// <summary>Example payload object (<c>examples[].payload</c>).</summary>
        public object? Payload { get; set; }
    }
}
