import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import * as api from "api-client";
import type {
  EdgeKind,
  EventPill,
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
}

interface UseTopologyDataResult {
  data: TopologyData | undefined;
  loading: boolean;
  /** Re-runs every fetch (event types + per-id details + metrics). */
  refresh: () => Promise<void>;
  lastUpdated?: Date;
  error?: string;
}

/**
 * Collects Topology data from existing read-only endpoints and rolls it into
 * `TopologyData`. Two-step fetch:
 *
 *   1. `getEventTypes()`           — one round-trip for the catalog.
 *   2. `getEventtypesEventtypeid()` per event type, in parallel — produces the
 *      producer/consumer arrays we need to render edges.
 *
 * Metrics (`getMetricsOverview`) hang off the time-range knob and re-fetch
 * independently when only `period` changes. We memoise the per-event-type
 * details by id so the same call isn't repeated as the user flips the
 * time range.
 *
 * The hook intentionally fails soft: if an individual event-type detail call
 * errors we drop that event type rather than blowing up the whole graph.
 */
export function useTopologyData({
  period,
  namespace,
}: UseTopologyDataOptions): UseTopologyDataResult {
  const [eventTypes, setEventTypes] = useState<api.EventType[]>([]);
  const [detailsById, setDetailsById] = useState<
    Record<string, api.EventTypeDetails>
  >({});
  const [metrics, setMetrics] = useState<api.MetricsOverview | undefined>();
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | undefined>();
  const [lastUpdated, setLastUpdated] = useState<Date | undefined>();

  // Lock subsequent renders from clobbering an in-flight refresh — the user
  // can spam the Refresh button or rapidly flip time ranges.
  const inFlight = useRef(0);

  const fetchCatalogAndDetails = useCallback(async () => {
    const client = new api.Client(api.CookieAuth());
    const all = await client.getEventTypes();
    const valid = all.filter((et) => !!et.id);
    setEventTypes(valid);

    const detailsEntries = await Promise.all(
      valid.map(async (et) => {
        try {
          const d = await client.getEventtypesEventtypeid(et.id!);
          return [et.id!, d] as const;
        } catch {
          return null;
        }
      }),
    );
    const next: Record<string, api.EventTypeDetails> = {};
    for (const entry of detailsEntries) {
      if (entry) {
        next[entry[0]] = entry[1];
      }
    }
    setDetailsById(next);
  }, []);

  const fetchMetrics = useCallback(async (p: api.Period) => {
    const client = new api.Client(api.CookieAuth());
    try {
      const m = await client.getMetricsOverview(p);
      setMetrics(m);
    } catch {
      // Metrics overlay is a nice-to-have — graph still renders without it.
      setMetrics(undefined);
    }
  }, []);

  const refresh = useCallback(async () => {
    const ticket = ++inFlight.current;
    setLoading(true);
    setError(undefined);
    try {
      await Promise.all([fetchCatalogAndDetails(), fetchMetrics(period)]);
      if (ticket === inFlight.current) {
        setLastUpdated(new Date());
      }
    } catch (err) {
      console.error("Failed to refresh topology", err);
      if (ticket === inFlight.current) {
        setError("Failed to load topology");
      }
    } finally {
      if (ticket === inFlight.current) {
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
    return buildTopology(eventTypes, detailsById, metrics, namespace);
  }, [eventTypes, detailsById, metrics, namespace]);

  return { data, loading, refresh, lastUpdated, error };
}

function buildTopology(
  eventTypes: api.EventType[],
  detailsById: Record<string, api.EventTypeDetails>,
  metrics: api.MetricsOverview | undefined,
  namespace: string | undefined,
): TopologyData {
  // 1. Filter by namespace if requested.
  const filteredTypes = namespace
    ? eventTypes.filter((et) => et.namespace === namespace)
    : eventTypes;

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

  const namespaces = new Set<string>();
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

  for (const et of filteredTypes) {
    if (!et.id) continue;
    if (et.namespace) namespaces.add(et.namespace);

    const detail = detailsById[et.id];
    const producers = detail?.producers ?? [];
    const consumers = detail?.consumers ?? [];

    // Publish side ------------------------------------------------------------
    for (const producer of producers) {
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
  }

  // 4. Materialise nodes with derived health.
  const nodes: TopologyNode[] = Array.from(nodeAccum.entries())
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

  // 5. Materialise edges with derived health.
  const edges: TopologyEdge[] = Array.from(edgeAccum.values()).map((e) => ({
    id: `${e.kind}::${e.endpointId}`,
    kind: e.kind,
    endpointId: e.endpointId,
    eventTypeIds: Array.from(e.eventTypeIds),
    messages: e.messages,
    health:
      e.failures > 0 ? "fail" : e.messages === 0 ? "idle" : "healthy",
  }));

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

  // 7. Summary strip counts.
  const summary = {
    endpoints: nodes.length,
    eventTypes: filteredTypes.length,
    edges: edges.length,
    edgesWithFailures: edges.filter((e) => e.health === "fail").length,
    namespaces: namespaces.size,
    producingEndpoints: nodes.filter((n) => n.publishCount > 0).length,
    consumingEndpoints: nodes.filter((n) => n.subscribeCount > 0).length,
  };

  return { nodes, edges, pills, summary };
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
