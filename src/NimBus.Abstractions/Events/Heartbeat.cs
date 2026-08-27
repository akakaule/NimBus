using System;
using System.ComponentModel;

namespace NimBus.Core.Events
{
    /// <summary>
    /// The platform heartbeat probe. The WebApp sends one per monitored endpoint, the
    /// SDK answers it automatically without a user handler, and the Resolver diverts
    /// the reply to the heartbeat store instead of the normal event audit trail.
    /// </summary>
    [Description("Platform liveness probe sent to an endpoint and answered by the SDK.")]
    public class Heartbeat : Event
    {
        /// <summary>
        /// A sample instance for catalog/documentation rendering.
        /// </summary>
        /// <remarks>
        /// Its position no longer matters. This used to have to sit above
        /// <see cref="EventTypeId"/> because <c>EventType.GetEventExample()</c> took the
        /// first public field of the type and would have returned the const instead;
        /// the lookup now resolves by name and type.
        /// </remarks>
        public static readonly Heartbeat Example = new Heartbeat
        {
            ForwardSendTime = new DateTime(2026, 8, 12, 9, 0, 0, DateTimeKind.Utc),
            ForwardReceivedTime = new DateTime(2026, 8, 12, 9, 0, 1, DateTimeKind.Utc),
            BackwardSendTime = new DateTime(2026, 8, 12, 9, 0, 1, DateTimeKind.Utc),
            BackwardReceivedTime = new DateTime(2026, 8, 12, 9, 0, 2, DateTimeKind.Utc),
            Endpoint = "AnalyticsEndpoint",
            SdkVersion = "1.0.0",
        };

        /// <summary>
        /// The <c>EventTypeId</c> heartbeat traffic travels under. Every side of the
        /// probe — WebApp sender, SDK auto-answer, Resolver divert — must agree on
        /// this exact string.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A reserved, dotted identifier rather than the bare type name: application
        /// event ids are unqualified CLR type names, which can never contain a dot,
        /// so no business event — typed or dynamically registered — can collide with
        /// this id. A collision would be silently destructive in both directions: the
        /// SDK would answer and complete the business event before its registered
        /// handler ran, and the Resolver would divert it away from the audit trail.
        /// </para>
        /// <para>
        /// Also spelled out as a literal rather than derived with <c>nameof</c>:
        /// callers that import this type under a <c>using</c> alias (both the WebApp
        /// and the Resolver do, to disambiguate it from the message-store Heartbeat)
        /// get the <em>alias</em> back from <c>nameof</c>, not the type name. That
        /// once silently put "CoreHeartbeat" on the wire, which the SDK's check never
        /// matched, so every adapter answered UnsupportedResponse.
        /// </para>
        /// </remarks>
        public const string EventTypeId = "NimBus.Platform.Heartbeat";

        /// <summary>Gets or sets the time the heartbeat is sent out in forward propagation.</summary>
        [Description("The time the heartbeat is sent out in forward propagation")]
        public DateTime ForwardSendTime { get; set; }

        /// <summary>Gets or sets the time the heartbeat is received in forward propagation.</summary>
        [Description("The time the heartbeat is received in forward propagation")]
        public DateTime ForwardReceivedTime { get; set; }

        /// <summary>Gets or sets the time the heartbeat is sent in backward propagation.</summary>
        [Description("The time the heartbeat is sent in backward propagation")]
        public DateTime BackwardSendTime { get; set; }

        /// <summary>Gets or sets the time the heartbeat is received in backward propagation.</summary>
        [Description("The time the heartbeat is received in backward propagation")]
        public DateTime BackwardReceivedTime { get; set; }

        /// <summary>Gets or sets the targeted endpoint.</summary>
        [Description("Targeted endpoint")]
        public string Endpoint { get; set; } = string.Empty;

        /// <summary>Gets or sets the NimBus SDK/Core informational version of the responding adapter.</summary>
        [Description("NimBus SDK/Core informational version of the responding adapter")]
        public string SdkVersion { get; set; } = string.Empty;
    }
}
