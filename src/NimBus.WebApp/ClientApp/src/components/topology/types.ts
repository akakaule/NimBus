// Domain model for the Topology page — derived client-side from
// /eventtypes + per-event-type /eventtypes/{id} producer/consumer arrays +
// /metrics/overview rollups. Kept in its own module so the data hook,
// graph, and inspector all share the same shape.

export type NodeHealth = "good" | "warn" | "bad" | "idle";

export interface TopologyNode {
  /** Stable endpoint id (matches `EndpointId` everywhere in the app). */
  id: string;
  /** Display name; falls back to id when the platform-config name is empty. */
  name: string;
  /** "Endpoint" today; reserved for "Sink"/"Source" if we ever expose them. */
  role: string;
  /** Distinct event types this endpoint publishes. */
  publishCount: number;
  /** Distinct event types this endpoint subscribes to. */
  subscribeCount: number;
  /** Sum of `published` rows for this endpoint over the selected window. */
  publishedMessages: number;
  /** Sum of `handled` rows for this endpoint over the selected window. */
  handledMessages: number;
  /** Sum of `failed` rows for this endpoint over the selected window. */
  failedMessages: number;
  /** Derived: bad > warn > idle > good. */
  health: NodeHealth;
}

export type EdgeKind = "publish" | "subscribe";

export interface TopologyEdge {
  /** Stable composite key — kind + endpoint id; used for React reconciliation. */
  id: string;
  /** Which direction this edge represents relative to the bus. */
  kind: EdgeKind;
  /**
   * For `publish` edges this is the producer endpoint; for `subscribe` edges
   * this is the consumer endpoint. The other end is always the bus hub.
   */
  endpointId: string;
  /** Event types collapsed into this edge (one endpoint may publish/subscribe many). */
  eventTypeIds: string[];
  /** Sum of message counts across the collapsed event types. */
  messages: number;
  /** Derived edge health — `idle` when no traffic, `fail` when any failures, else `healthy`. */
  health: "healthy" | "warn" | "fail" | "idle";
}

export interface EventPill {
  /** Event-type id (becomes both the label and the deep-link target). */
  id: string;
  /** Display label — usually the unqualified name; falls back to id. */
  label: string;
  /** The endpoint this pill should anchor against in the graph layout. */
  anchorEndpointId: string;
  /** Edge kind we should attach to (publish vs subscribe). */
  kind: EdgeKind;
  /** Tooltip body, e.g. "ErpEndpoint → CrmEndpoint, AnalyticsEndpoint · 7,412 / 1h". */
  tooltip: string;
}

export type FlowEdgeHealth = "live" | "warn" | "fail" | "idle";

/**
 * Bipartite (publisher → subscriber) edge for the Flow view. Unlike
 * `TopologyEdge` which collapses every event-type into a hub-relative
 * spoke, a `FlowEdge` resolves the actual sender/receiver pair so the
 * operator can see *which* endpoint receives *which* publisher's stream
 * directly, without the bus as a visual middleman.
 */
export interface FlowEdge {
  /** Stable composite key — `from::to`; used for React reconciliation. */
  id: string;
  /** Producer endpoint id. */
  from: string;
  /** Consumer endpoint id. */
  to: string;
  /** Event types that flow on this route. */
  eventTypeIds: string[];
  /** Estimated traffic for this route (handled-side, summed across event types). */
  messages: number;
  /** Estimated failures on this route. */
  failures: number;
  /** Derived health — `idle` no traffic · `fail` any failures · `warn` reserved · `live` otherwise. */
  health: FlowEdgeHealth;
  /** Pre-rendered tooltip body for the ribbon hover. */
  tooltip: string;
}

export interface TopologyData {
  nodes: TopologyNode[];
  edges: TopologyEdge[];
  pills: EventPill[];
  /** Producer→Consumer routes for the Flow view. Independent of `edges`. */
  flowEdges: FlowEdge[];
  /** Aggregate counts for the summary strip; cheap to compute alongside the rest. */
  summary: {
    endpoints: number;
    eventTypes: number;
    edges: number;
    edgesWithFailures: number;
    namespaces: number;
    producingEndpoints: number;
    consumingEndpoints: number;
  };
}
