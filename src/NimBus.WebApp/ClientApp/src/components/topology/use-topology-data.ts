import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import * as api from "api-client";
import type {
  EdgeKind,
  EventPill,
  FlowEdge,
  FlowEdgeHealth,
  NodeHealth,
  TopologyData,
  TopologyEdge,
  TopologyNode,
} from "./types";

const MAX_EVENT_PILLS = 6;

interface UseTopologyDataOptions {
  /** Selected time window for the metrics overlay. */
  period: api.Period;
  /** Optional namespace filter — when set, only event types matching are included. */
  namespace?: string;
  /**
   * Optional endpoint focus — when set, the graph narrows to `endpoint` and
   * its pub-sub counterparties (anyone that consumes from `endpoint` or
   * produces something `endpoint` consumes). Co-producers / co-consumers
   * with no pub-sub link to `endpoint` are excluded.
   */
  endpoint?: string;
  /**
   * Optional search query — hides nodes and event types that neither match
   * the text themselves nor participate in a matching event type. Trimmed
   * empty string is treated as "no filter".
   */
  searchText?: string;
  /** When true, edges with zero traffic — and the nodes they leave behind — are removed. */
  hideIdleEdges?: boolean;
}

interface UseTopologyDataResult {
  data: TopologyData | undefined;
  loading: boolean;
  /** Re-runs every fetch (event-type catalog + metrics). */
  refresh: () => Promise<void>;
  lastUpdated?: Date;
  error?: string;
  /** All namespaces seen across the unfiltered event-type catalog. */
  allNamespaces: string[];
  /** All endpoints seen as producers or consumers across the unfiltered catalog. */
  allEndpoints: string[];
}

/**
 * Collects Topology data from existing read-only endpoints and rolls it into
 * `TopologyData`. Single round-trip:
 *
 *   1. `getEventTypes()` — one call for the catalog. Each catalog entry already
 *      carries the `producers`/`consumers` arrays we need to render edges, so
 *      we build `detailsById` straight from that response rather than fanning
 *      out a per-event-type `getEventtypesEventtypeid()` detail call.
 *
 * Metrics (`getMetricsOverview`) hang off the time-range knob and re-fetch
 * independently when only `period` changes. The per-event-type details don't
 * depend on the time window, so we reuse them as the user flips the range.
 */
export function useTopologyData({
  period,
  namespace,
  endpoint,
  searchText,
  hideIdleEdges,
}: UseTopologyDataOptions): UseTopologyDataResult {
  const [eventTypes, setEventTypes] = useState<api.EventType[]>([]);
  const [detailsById, setDetailsById] = useState<
    Record<string, api.EventTypeDetails>
  >({});
  const [metrics, setMetrics] = useState<api.MetricsOverview | undefined>();
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | undefined>();
  const [lastUpdated, setLastUpdated] = useState<Date | undefined>();

  // Separate tickets per fetch path so a metrics-only refetch (period flip)
  // doesn't invalidate an in-flight refresh's epilogue, and vice versa.
  const refreshTicket = useRef(0);
  const metricsTicket = useRef(0);

  const fetchCatalogAndDetails = useCallback(async () => {
    const client = new api.Client(api.CookieAuth());
    const all = await client.getEventTypes();
    const valid = all.filter((et) => !!et.id);
    setEventTypes(valid);

    // The catalog entries already carry their producer/consumer arrays, so
    // build the detail map straight from this response — no per-event-type
    // round trip.
    const next: Record<string, api.EventTypeDetails> = {};
    for (const et of valid) {
      next[et.id!] = new api.EventTypeDetails({
        producers: et.producers ?? [],
        consumers: et.consumers ?? [],
      });
    }
    setDetailsById(next);
  }, []);

  const fetchMetrics = useCallback(async (p: api.Period) => {
    const ticket = ++metricsTicket.current;
    const client = new api.Client(api.CookieAuth());
    try {
      const m = await client.getMetricsOverview(p);
      if (ticket === metricsTicket.current) {
        setMetrics(m);
      }
    } catch {
      if (ticket === metricsTicket.current) {
        setMetrics(undefined);
      }
    }
  }, []);

  const refresh = useCallback(async () => {
    const ticket = ++refreshTicket.current;
    setLoading(true);
    setError(undefined);
    try {
      await Promise.all([fetchCatalogAndDetails(), fetchMetrics(period)]);
      if (ticket === refreshTicket.current) {
        setLastUpdated(new Date());
      }
    } catch (err) {
      console.error("Failed to refresh topology", err);
      if (ticket === refreshTicket.current) {
        setError("Failed to load topology");
      }
    } finally {
      if (ticket === refreshTicket.current) {
        setLoading(false);
      }
    }
  }, [fetchCatalogAndDetails, fetchMetrics, period]);

  // Initial load + reload on period change. We refetch metrics on period
  // change but reuse the cached event-type details since they don't depend
  // on the time window.
  useEffect(() => {
    let cancelled = false;
    if (eventTypes.length === 0) {
      void refresh();
    } else {
      setLoading(true);
      void fetchMetrics(period).finally(() => {
        if (!cancelled) {
          setLoading(false);
          setLastUpdated(new Date());
        }
      });
    }
    return () => {
      cancelled = true;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [period]);

  const data = useMemo<TopologyData | undefined>(() => {
    if (eventTypes.length === 0) return undefined;
    return buildTopology(eventTypes, detailsById, metrics, namespace, endpoint, searchText, hideIdleEdges);
  }, [eventTypes, detailsById, metrics, namespace, endpoint, searchText, hideIdleEdges]);

  // Unfiltered option lists for the filter UI — derived from the full catalog
  // so picking a namespace doesn't make the namespace dropdown collapse to one.
  const allNamespaces = useMemo(() => {
    const out = new Set<string>();
    for (const et of eventTypes) {
      if (et.namespace) out.add(et.namespace);
    }
    return Array.from(out).sort();
  }, [eventTypes]);

  const allEndpoints = useMemo(() => {
    const out = new Set<string>();
    for (const et of eventTypes) {
      const det = et.id ? detailsById[et.id] : undefined;
      det?.producers?.forEach((p) => out.add(p));
      det?.consumers?.forEach((c) => out.add(c));
    }
    return Array.from(out).sort();
  }, [eventTypes, detailsById]);

  return { data, loading, refresh, lastUpdated, error, allNamespaces, allEndpoints };
}

function buildTopology(
  eventTypes: api.EventType[],
  detailsById: Record<string, api.EventTypeDetails>,
  metrics: api.MetricsOverview | undefined,
  namespace: string | undefined,
  endpoint: string | undefined,
  searchText: string | undefined,
  hideIdleEdges: boolean | undefined,
): TopologyData {
  // 1a. Namespace filter.
  const namespaceFiltered = namespace
    ? eventTypes.filter((et) => et.namespace === namespace)
    : eventTypes;

  // 1b. Search filter. Keep an event type if its name/id matches OR any of
  //     its producers/consumers matches by name. Empty/whitespace = no filter.
  const lowerSearch = searchText?.trim().toLowerCase() ?? "";
  const searchFiltered = lowerSearch
    ? namespaceFiltered.filter((et) => {
        if ((et.name ?? "").toLowerCase().includes(lowerSearch)) return true;
        if ((et.id ?? "").toLowerCase().includes(lowerSearch)) return true;
        const det = et.id ? detailsById[et.id] : undefined;
        if (det?.producers?.some((p) => p.toLowerCase().includes(lowerSearch))) return true;
        if (det?.consumers?.some((c) => c.toLowerCase().includes(lowerSearch))) return true;
        return false;
      })
    : namespaceFiltered;

  // 1c. Endpoint focus — restrict to event types `endpoint` produces/consumes,
  //     and pre-compute the set of pub-sub counterparties so the build loop
  //     can drop unrelated co-producers / co-consumers.
  let relevantTypes = searchFiltered;
  let allowedEndpoints: Set<string> | undefined;
  if (endpoint) {
    relevantTypes = searchFiltered.filter((et) => {
      const det = et.id ? detailsById[et.id] : undefined;
      const producers = det?.producers ?? [];
      const consumers = det?.consumers ?? [];
      return producers.includes(endpoint) || consumers.includes(endpoint);
    });

    allowedEndpoints = new Set([endpoint]);
    for (const et of relevantTypes) {
      const det = et.id ? detailsById[et.id] : undefined;
      const producers = det?.producers ?? [];
      const consumers = det?.consumers ?? [];
      if (producers.includes(endpoint)) {
        for (const c of consumers) allowedEndpoints.add(c);
      }
      if (consumers.includes(endpoint)) {
        for (const p of producers) allowedEndpoints.add(p);
      }
    }
  }

  // 2. Index metrics so we can answer "how many published / handled / failed
  //    for endpoint X event type Y in this window?" in O(1).
  const published = indexCounts(metrics?.published);
  const handled = indexCounts(metrics?.handled);
  const failed = indexCounts(metrics?.failed);

  // 3. Walk every event type and accumulate node + edge state.
  type NodeAccum = {
    publishedTypeIds: Set<string>;
    subscribedTypeIds: Set<string>;
    publishedMessages: number;
    handledMessages: number;
    failedMessages: number;
  };
  const nodeAccum = new Map<string, NodeAccum>();
  const ensureNode = (id: string) => {
    let n = nodeAccum.get(id);
    if (!n) {
      n = {
        publishedTypeIds: new Set(),
        subscribedTypeIds: new Set(),
        publishedMessages: 0,
        handledMessages: 0,
        failedMessages: 0,
      };
      nodeAccum.set(id, n);
    }
    return n;
  };

  // We collapse multi-event-type edges between the same (endpoint, kind) into
  // a single rendered edge by keying on `kind + endpoint`.
  type EdgeAccum = {
    kind: EdgeKind;
    endpointId: string;
    eventTypeIds: Set<string>;
    messages: number;
    failures: number;
  };
  const edgeAccum = new Map<string, EdgeAccum>();
  const ensureEdge = (kind: EdgeKind, endpointId: string) => {
    const key = `${kind}::${endpointId}`;
    let e = edgeAccum.get(key);
    if (!e) {
      e = { kind, endpointId, eventTypeIds: new Set(), messages: 0, failures: 0 };
      edgeAccum.set(key, e);
    }
    return e;
  };

  // Tracks (endpoint, eventType, kind) → traffic for picking the top event pills.
  const pillCandidates: Array<{
    eventTypeId: string;
    label: string;
    endpointId: string;
    kind: EdgeKind;
    messages: number;
    failures: number;
    producers: string[];
    consumers: string[];
  }> = [];

  // (producer, consumer) → bipartite route for the Flow view. We accumulate
  // one entry per producer→consumer pair (across all event types they share),
  // and approximate per-route traffic by attributing the consumer's
  // handled-count for an event type evenly across that event type's producers.
  // This is the best estimate available from the existing /metrics endpoint
  // since traffic isn't tagged with source-endpoint on the receive side.
  type FlowAccum = {
    from: string;
    to: string;
    eventTypeIds: Set<string>;
    eventTypeLabels: Record<string, string>;
    messages: number;
    failures: number;
  };
  const flowAccum = new Map<string, FlowAccum>();
  const ensureFlow = (from: string, to: string) => {
    const key = `${from}::${to}`;
    let f = flowAccum.get(key);
    if (!f) {
      f = {
        from,
        to,
        eventTypeIds: new Set(),
        eventTypeLabels: {},
        messages: 0,
        failures: 0,
      };
      flowAccum.set(key, f);
    }
    return f;
  };

  for (const et of relevantTypes) {
    if (!et.id) continue;

    const detail = detailsById[et.id];
    const producers = detail?.producers ?? [];
    const consumers = detail?.consumers ?? [];

    // Publish side ------------------------------------------------------------
    for (const producer of producers) {
      if (allowedEndpoints && !allowedEndpoints.has(producer)) continue;
      const node = ensureNode(producer);
      node.publishedTypeIds.add(et.id);
      const msgs = published[`${producer}::${et.id}`] ?? 0;
      const fails = failed[`${producer}::${et.id}`] ?? 0;
      node.publishedMessages += msgs;

      const edge = ensureEdge("publish", producer);
      edge.eventTypeIds.add(et.id);
      edge.messages += msgs;
      edge.failures += fails;

      pillCandidates.push({
        eventTypeId: et.id,
        label: et.name || et.id,
        endpointId: producer,
        kind: "publish",
        messages: msgs,
        failures: fails,
        producers,
        consumers,
      });
    }

    // Subscribe side ----------------------------------------------------------
    for (const consumer of consumers) {
      if (allowedEndpoints && !allowedEndpoints.has(consumer)) continue;
      const node = ensureNode(consumer);
      node.subscribedTypeIds.add(et.id);
      const msgs = handled[`${consumer}::${et.id}`] ?? 0;
      const fails = failed[`${consumer}::${et.id}`] ?? 0;
      node.handledMessages += msgs;
      node.failedMessages += fails;

      const edge = ensureEdge("subscribe", consumer);
      edge.eventTypeIds.add(et.id);
      edge.messages += msgs;
      edge.failures += fails;
    }

    // Flow side ---------------------------------------------------------------
    // For every (producer, consumer) pair that share this event type, record
    // a route. Per-route traffic is the consumer's handled count for this
    // event type, evenly split across the producers (best estimate available
    // from the existing metrics — receive-side counts aren't tagged with the
    // source endpoint).
    const allowedProducers = allowedEndpoints
      ? producers.filter((p) => allowedEndpoints!.has(p))
      : producers;
    const allowedConsumers = allowedEndpoints
      ? consumers.filter((c) => allowedEndpoints!.has(c))
      : consumers;
    if (allowedProducers.length > 0 && allowedConsumers.length > 0) {
      const producerShare = 1 / allowedProducers.length;
      const label = et.name || et.id;
      for (const consumer of allowedConsumers) {
        const consumerMsgs = handled[`${consumer}::${et.id}`] ?? 0;
        const consumerFails = failed[`${consumer}::${et.id}`] ?? 0;
        for (const producer of allowedProducers) {
          const flow = ensureFlow(producer, consumer);
          flow.eventTypeIds.add(et.id);
          flow.eventTypeLabels[et.id] = label;
          flow.messages += consumerMsgs * producerShare;
          flow.failures += consumerFails * producerShare;
        }
      }
    }
  }

  // 4. Materialise nodes with derived health.
  const allNodes: TopologyNode[] = Array.from(nodeAccum.entries())
    .map(([id, n]) => {
      const health: NodeHealth = classifyNodeHealth(
        n.failedMessages,
        n.publishedMessages + n.handledMessages,
      );
      return {
        id,
        name: id,
        role: "Endpoint",
        publishCount: n.publishedTypeIds.size,
        subscribeCount: n.subscribedTypeIds.size,
        publishedMessages: n.publishedMessages,
        handledMessages: n.handledMessages,
        failedMessages: n.failedMessages,
        health,
      };
    })
    .sort((a, b) => a.name.localeCompare(b.name));

  // 5. Materialise edges with derived health. Drop idle edges when the user
  //    has asked to see only edges with traffic.
  const allEdges: TopologyEdge[] = Array.from(edgeAccum.values()).map((e) => ({
    id: `${e.kind}::${e.endpointId}`,
    kind: e.kind,
    endpointId: e.endpointId,
    eventTypeIds: Array.from(e.eventTypeIds),
    messages: e.messages,
    health:
      e.failures > 0 ? "fail" : e.messages === 0 ? "idle" : "healthy",
  }));
  const edges = hideIdleEdges
    ? allEdges.filter((e) => e.health !== "idle")
    : allEdges;

  // 5b. Drop nodes that have no remaining edges — otherwise hiding idle edges
  //     leaves orphan endpoint cards floating with no connections.
  const activeEndpointIds = new Set(edges.map((e) => e.endpointId));
  const nodes = allNodes.filter((n) => activeEndpointIds.has(n.id));

  // 5c. Materialise the bipartite flow edges. Same idle-filter semantics as
  //     hub edges — if the operator asked to hide idle traffic, drop flow
  //     ribbons with zero messages so the canvas focuses on live routes.
  const allFlowEdges: FlowEdge[] = Array.from(flowAccum.values()).map((f) => {
    const eventTypeIds = Array.from(f.eventTypeIds).sort();
    const messages = Math.round(f.messages);
    const failures = Math.round(f.failures);
    const health: FlowEdgeHealth =
      failures > 0 ? "fail" : messages === 0 ? "idle" : "live";
    return {
      id: `${f.from}::${f.to}`,
      from: f.from,
      to: f.to,
      eventTypeIds,
      messages,
      failures,
      health,
      tooltip: buildFlowTooltip(f.from, f.to, eventTypeIds, f.eventTypeLabels, messages, failures),
    };
  });
  const flowEdges = hideIdleEdges
    ? allFlowEdges.filter((f) => f.health !== "idle")
    : allFlowEdges;

  // 6. Pick the top event pills by traffic (skipping idle edges).
  const pills: EventPill[] = pillCandidates
    .filter((c) => c.messages > 0)
    .sort((a, b) => b.messages - a.messages)
    .slice(0, MAX_EVENT_PILLS)
    .map((c) => ({
      id: c.eventTypeId,
      label: c.label,
      anchorEndpointId: c.endpointId,
      kind: c.kind,
      tooltip: buildPillTooltip(c),
    }));

  // 7. Summary strip counts — all derived from the visible nodes and edges so
  //    the numbers in the strip match what's actually rendered.
  const visibleTypeIds = new Set<string>();
  for (const e of edges) {
    for (const id of e.eventTypeIds) visibleTypeIds.add(id);
  }
  const visibleNamespaces = new Set<string>();
  for (const et of relevantTypes) {
    if (et.id && visibleTypeIds.has(et.id) && et.namespace) {
      visibleNamespaces.add(et.namespace);
    }
  }
  const producingEndpoints = new Set<string>();
  const consumingEndpoints = new Set<string>();
  for (const e of edges) {
    if (e.kind === "publish") producingEndpoints.add(e.endpointId);
    else consumingEndpoints.add(e.endpointId);
  }

  const summary = {
    endpoints: nodes.length,
    eventTypes: visibleTypeIds.size,
    edges: edges.length,
    edgesWithFailures: edges.filter((e) => e.health === "fail").length,
    namespaces: visibleNamespaces.size,
    producingEndpoints: producingEndpoints.size,
    consumingEndpoints: consumingEndpoints.size,
  };

  return { nodes, edges, pills, flowEdges, summary };
}

function buildFlowTooltip(
  from: string,
  to: string,
  eventTypeIds: string[],
  labels: Record<string, string>,
  messages: number,
  failures: number,
): string {
  const head = `${from} → ${to}`;
  const trafficBits: string[] = [];
  if (messages > 0) trafficBits.push(`${messages.toLocaleString()} ok`);
  if (failures > 0) trafficBits.push(`${failures.toLocaleString()} failed`);
  if (trafficBits.length === 0) trafficBits.push("idle");
  const traffic = trafficBits.join(" · ");
  const previewLabels = eventTypeIds.slice(0, 3).map((id) => labels[id] ?? id);
  const eventsSummary =
    previewLabels.length === 0
      ? "no event types"
      : `events: ${previewLabels.join(", ")}${
          eventTypeIds.length > previewLabels.length
            ? ` +${eventTypeIds.length - previewLabels.length}`
            : ""
        }`;
  return `${head} · ${traffic} · ${eventsSummary}`;
}

function indexCounts(
  rows: api.EndpointEventTypeMessageCount[] | undefined,
): Record<string, number> {
  const out: Record<string, number> = {};
  for (const r of rows ?? []) {
    if (!r.endpointId || !r.eventTypeId) continue;
    const key = `${r.endpointId}::${r.eventTypeId}`;
    out[key] = (out[key] ?? 0) + (r.count ?? 0);
  }
  return out;
}

function classifyNodeHealth(failed: number, totalTraffic: number): NodeHealth {
  if (failed > 0) return "bad";
  if (totalTraffic === 0) return "idle";
  return "good";
  // Note: we don't currently surface a `warn` (deferred-above-threshold) state
  // because MetricsOverview doesn't expose deferred counts. Wire that in if /
  // when the API does.
}

function buildPillTooltip(c: {
  label: string;
  endpointId: string;
  kind: EdgeKind;
  messages: number;
  producers: string[];
  consumers: string[];
}): string {
  if (c.kind === "publish") {
    const consumers = c.consumers.length === 0 ? "no consumers" : c.consumers.join(", ");
    return `${c.label} · ${c.endpointId} → ${consumers} · ${c.messages.toLocaleString()} msgs`;
  }
  const producers = c.producers.length === 0 ? "no producers" : c.producers.join(", ");
  return `${c.label} · ${producers} → ${c.endpointId} · ${c.messages.toLocaleString()} msgs`;
}
