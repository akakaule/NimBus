using System;
using System.Collections.Generic;
using System.Text;

namespace NimBus.MessageStore
{
    public class MessageAuditEntity
    {
        public string AuditorName { get; set; }
        public DateTime AuditTimestamp { get; set; }
        public MessageAuditType AuditType { get; set; }
        public string? Comment { get; set; }

        /// <summary>
        /// True when the audited action was rejected by the authorization layer
        /// (the user attempted the action but did not have the required permission).
        /// Defaults to <c>false</c> so legacy rows project unchanged.
        /// </summary>
        public bool AccessDenied { get; set; }

        /// <summary>
        /// Optional structured context for the action: search filter JSON,
        /// ResubmitWithChanges body, or any other payload the caller wants
        /// preserved alongside the audit row. Truncated to ~4 KB by the
        /// <see cref="NimBus.WebApp.Services"/> audit writer to stay within
        /// every provider's column / document size budget.
        /// </summary>
        public string? Data { get; set; }

        /// <summary>
        /// Event id the audit row is associated with. Mirrors the <c>eventId</c>
        /// argument supplied to <see cref="Abstractions.IMessageTrackingStore.StoreMessageAudit"/>
        /// so downstream readers do not have to join on a side channel.
        /// </summary>
        public string? EventId { get; set; }

        /// <summary>
        /// Endpoint id the audit row is associated with. Mirrors the <c>endpointId</c>
        /// argument supplied to <see cref="Abstractions.IMessageTrackingStore.StoreMessageAudit"/>.
        /// </summary>
        public string? EndpointId { get; set; }

        /// <summary>
        /// CloudEvents <c>id</c> attribute when the tracked message was received or
        /// published as a CloudEvent, otherwise <c>null</c>. Preserved so CloudEvents
        /// identity surfaces in the message store / management UI. Defaults to
        /// <c>null</c> so legacy rows and native messages project unchanged.
        /// </summary>
        public string? CloudEventId { get; set; }

        /// <summary>CloudEvents <c>source</c> attribute, or <c>null</c> for a native message.</summary>
        public string? CloudEventSource { get; set; }

        /// <summary>CloudEvents <c>type</c> attribute, or <c>null</c> for a native message.</summary>
        public string? CloudEventType { get; set; }

        /// <summary>CloudEvents <c>subject</c> attribute, or <c>null</c> for a native message.</summary>
        public string? CloudEventSubject { get; set; }
    }

    public enum MessageAuditType
    {
        Resubmit,
        ResubmitWithChanges,
        Skip,
        Retry,
        Comment,
        CompleteHandoff,
        FailHandoff,

        /// <summary>Operator searched the event store with a filter (records the filter as Data).</summary>
        SearchEvents,

        /// <summary>Operator opened the event-details page for a specific event.</summary>
        GetEventDetails,

        /// <summary>Operator opened the endpoint-details page for a specific endpoint.</summary>
        GetEndpointDetails,

        /// <summary>Operator enabled an endpoint subscription.</summary>
        EnableEndpoint,

        /// <summary>Operator disabled an endpoint subscription.</summary>
        DisableEndpoint,

        /// <summary>Operator purged messages from an endpoint / subscription / session.</summary>
        PurgeMessages,

        /// <summary>Operator composed and published a new event from the WebApp.</summary>
        Compose,
    }
}
