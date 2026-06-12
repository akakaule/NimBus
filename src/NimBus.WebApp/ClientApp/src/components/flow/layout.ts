// Layout engine for the Live Flow page (spec 020, FR-002). Consumes the
// Topology page's already-derived TopologyData and produces the deterministic
// four-column layered layout: producers → topics → consumers → platform.
// Deliberately pure — no DOM, no clock, no randomness — so vitest can pin the
// geometry exactly and the page can memoise the result by input identity.
// The animator addresses routes by the stable ids built here; changing any id
// scheme is a breaking change for it.

import type { TopologyData, TopologyNode } from "components/topology/types";
import type {
  EndpointRouteIndex,
  FlowLayout,
  FlowNode,
  FlowRoute,
  FlowRouteKind,
} from "./types";

export interface LayoutOptions {
  /** Endpoints to render; undefined = all. Both producer and consumer roles of a visible endpoint render. */
  visibleEndpointIds?: ReadonlySet<string>;
}

// Fixed column geometry. The canvas is an SVG viewBox, so these absolute
// numbers scale with the container — keeping them constant (instead of
// measuring text) is what makes route paths reproducible in tests and stable
// across renders. Topic chips get extra width because endpoint ids run longer
// than display names.
const COL_X = { producer: 20, topic: 330, consumer: 660, platform: 990 } as const;
const COL_W = { producer: 220, topic: 240, consumer: 220, platform: 220 } as const;
const ENDPOINT_H = 64;
const TOPIC_H = 40;
const PLATFORM_H = 72;
const TOP_MARGIN = 20;
const GAP = 16;
const CANVAS_W = 1240;
const MIN_CANVAS_H = 600;

// "Azure Service Bus" container around the topic column: a header band above
// the chips for the title/subtitle, and padding around the chip stack.
const SB_HEADER_H = 40;
const SB_PAD = 12;

// Well-known node id for the Resolver — the single platform fixture, rendered
// to the right of the consuming endpoints where every outcome converges.
const RESOLVER_PLATFORM = "platform::resolver";

/**
 * Builds the full flow layout (nodes, routes, byEndpoint index) from the
 * topology catalog. Deterministic: identical input always yields a
 * deep-equal layout regardless of catalog array order.
 */
export function buildFlowLayout(
  data: TopologyData,
  opts?: LayoutOptions,
): FlowLayout {
  const visible = opts?.visibleEndpointIds;
  const isVisible = (id: string): boolean =>
    visible === undefined || visible.has(id);

  // An endpoint with both roles appears in BOTH columns (the convention
  // proven by topology-flow.tsx), so the two populations are derived
  // independently rather than partitioning the catalog. Sort by the
  // role-relevant traffic so the busiest endpoints surface at the top where
  // the operator's eye lands first; ties fall back to name for stability.
  const producers = data.nodes
    .filter((n) => n.publishCount > 0 && isVisible(n.id))
    .sort(
      (a, b) => b.publishedMessages - a.publishedMessages || compareByName(a, b),
    );
  const consumers = data.nodes
    .filter((n) => n.subscribeCount > 0 && isVisible(n.id))
    .sort(
      (a, b) => b.handledMessages - a.handledMessages || compareByName(a, b),
    );

  const producerNodes = producers.map((n) =>
    endpointNode("producer", n, `publishes ${n.publishCount} event type(s)`),
  );
  const consumerNodes = consumers.map((n) =>
    endpointNode("consumer", n, `handles ${n.subscribeCount} event type(s)`),
  );

  // One topic per producing endpoint is a NimBus platform invariant, so the
  // chip is titled with the endpoint id directly — no separate topic catalog
  // exists to consult. Chips mirror producer order so each chip sits roughly
  // beside its producer.
  const topicNodes: FlowNode[] = producers.map((n) => ({
    id: `topic::${n.id}`,
    kind: "topic",
    endpointId: n.id,
    title: n.id,
    x: COL_X.topic,
    y: 0,
    w: COL_W.topic,
    h: TOPIC_H,
    health: "good",
  }));

  // The Resolver is the only platform fixture — every consumer's outcomes
  // converge on it. Present in every deployment and exempt from filtering.
  const platformNodes: FlowNode[] = [platformNode(RESOLVER_PLATFORM, "Resolver")];

  stackColumn(producerNodes);
  // Topic chips start below the Service Bus header band that frames them.
  stackColumn(topicNodes, TOP_MARGIN + SB_HEADER_H);
  stackColumn(consumerNodes);
  stackColumn(platformNodes);
  // Sit the lone Resolver fixture beside the consumer column it collects
  // outcomes from, rather than pinned to the top margin.
  centerColumn(platformNodes, columnCenter(consumerNodes));

  // The Service Bus container wraps the topic chips with padding plus the
  // header band; only present when there are topics to frame.
  let serviceBus: FlowLayout["serviceBus"];
  if (topicNodes.length > 0) {
    const last = topicNodes[topicNodes.length - 1];
    serviceBus = {
      x: COL_X.topic - SB_PAD,
      y: TOP_MARGIN,
      w: COL_W.topic + SB_PAD * 2,
      h: last.y + last.h + SB_PAD - TOP_MARGIN,
    };
  }

  const nodes: FlowNode[] = [
    ...producerNodes,
    ...topicNodes,
    ...consumerNodes,
    ...platformNodes,
  ];
  const nodeById = new Map(nodes.map((n) => [n.id, n]));

  const routes: FlowRoute[] = [];

  // publish: producer → its own topic. Event types come from the flow edges
  // (not publishCount) because flowEdges are the only place TopologyData ties
  // concrete event-type ids to a producing endpoint; types nobody subscribes
  // to simply don't decorate the route.
  const eventTypesByProducer = new Map<string, Set<string>>();
  for (const edge of data.flowEdges) {
    let set = eventTypesByProducer.get(edge.from);
    if (!set) {
      set = new Set<string>();
      eventTypesByProducer.set(edge.from, set);
    }
    for (const eventTypeId of edge.eventTypeIds) set.add(eventTypeId);
  }
  for (const n of producers) {
    routes.push(
      makeRoute(
        "publish",
        nodeById.get(`producer::${n.id}`)!,
        nodeById.get(`topic::${n.id}`)!,
        [...(eventTypesByProducer.get(n.id) ?? [])].sort(),
      ),
    );
  }

  // deliver: topic → consumer, one route per flow edge whose BOTH ends
  // survived filtering. An edge referencing a hidden or unknown endpoint is
  // dropped silently — the page's filter UI owns messaging about hidden
  // traffic, the layout just stays consistent.
  const producerIds = new Set(producers.map((n) => n.id));
  const consumerIds = new Set(consumers.map((n) => n.id));
  const deliverRoutes: FlowRoute[] = [];
  for (const edge of data.flowEdges) {
    if (!producerIds.has(edge.from) || !consumerIds.has(edge.to)) continue;
    deliverRoutes.push(
      makeRoute(
        "deliver",
        nodeById.get(`topic::${edge.from}`)!,
        nodeById.get(`consumer::${edge.to}`)!,
        [...edge.eventTypeIds],
      ),
    );
  }
  // Sorting by id makes the output independent of flowEdges array order —
  // part of the determinism contract — and pre-sorts byEndpoint.deliver.
  deliverRoutes.sort(compareStrings((r) => r.id));
  routes.push(...deliverRoutes);

  // outcome: every consumer reports to the Resolver, which sits to the right
  // of the consumer column. Outcome routes carry no event types: outcomes are
  // status records, not typed business events.
  const resolver = nodeById.get(RESOLVER_PLATFORM)!;
  for (const n of consumers) {
    routes.push(
      makeRoute("outcome", nodeById.get(`consumer::${n.id}`)!, resolver, []),
    );
  }

  // byEndpoint: the animator's entry point — it receives semantic events
  // keyed by endpoint id and needs the concrete route ids without scanning
  // the route list per event.
  const byEndpoint: Record<string, EndpointRouteIndex> = {};
  for (const n of consumers) {
    const consumerNodeId = `consumer::${n.id}`;
    byEndpoint[n.id] = {
      nodeId: consumerNodeId,
      deliver: deliverRoutes
        .filter((r) => r.toNodeId === consumerNodeId)
        .map((r) => r.id),
      outcome: `outcome::${consumerNodeId}::${RESOLVER_PLATFORM}`,
    };
  }

  // Height tracks the tallest column so big catalogs scroll instead of
  // squeezing; the 600 floor keeps small/empty layouts from collapsing into
  // a squashed band when the SVG scales to its container.
  let maxBottom = 0;
  for (const n of nodes) maxBottom = Math.max(maxBottom, n.y + n.h);
  if (serviceBus) maxBottom = Math.max(maxBottom, serviceBus.y + serviceBus.h);
  const height = Math.max(MIN_CANVAS_H, maxBottom + TOP_MARGIN);

  return { nodes, routes, width: CANVAS_W, height, byEndpoint, serviceBus };
}

/**
 * Helper the page uses for the default filter: top-N endpoint ids by
 * (publishedMessages + handledMessages), ties broken by name. Combined
 * traffic rather than per-role so a both-role endpoint's full footprint
 * counts once; the name tiebreak keeps the default view stable on quiet
 * platforms where most totals are zero.
 */
export function topEndpointIds(data: TopologyData, n: number): string[] {
  if (n <= 0) return [];
  return [...data.nodes]
    .sort(
      (a, b) =>
        b.publishedMessages +
          b.handledMessages -
          (a.publishedMessages + a.handledMessages) || compareByName(a, b),
    )
    .slice(0, n)
    .map((node) => node.id);
}

// ----- internals -----------------------------------------------------------

function endpointNode(
  kind: "producer" | "consumer",
  n: TopologyNode,
  subtitle: string,
): FlowNode {
  return {
    id: `${kind}::${n.id}`,
    kind,
    endpointId: n.id,
    title: n.name,
    subtitle,
    x: COL_X[kind],
    y: 0, // assigned by stackColumn
    w: COL_W[kind],
    h: ENDPOINT_H,
    // Endpoint health travels onto both column instances; topics/platform
    // stay "good" because the catalog has no health signal for them.
    health: n.health,
  };
}

function platformNode(id: string, title: string): FlowNode {
  return {
    id,
    kind: "platform",
    title,
    x: COL_X.platform,
    y: 0,
    w: COL_W.platform,
    h: PLATFORM_H,
    health: "good",
  };
}

/**
 * Vertical stacking with a fixed top margin and gap. Mutates y in place —
 * safe because every node was created inside buildFlowLayout, never
 * caller-owned.
 */
function stackColumn(column: FlowNode[], startY: number = TOP_MARGIN): void {
  let y = startY;
  for (const node of column) {
    node.y = y;
    y += node.h + GAP;
  }
}

/** Vertical center of an already-stacked column; top margin when empty. */
function columnCenter(column: FlowNode[]): number {
  if (column.length === 0) return TOP_MARGIN;
  const top = column[0].y;
  const last = column[column.length - 1];
  return (top + last.y + last.h) / 2;
}

/**
 * Shifts an already-stacked column so its block centers on `center`, never
 * rising above the top margin. Mutates y in place — safe because every node
 * was created inside buildFlowLayout.
 */
function centerColumn(column: FlowNode[], center: number): void {
  if (column.length === 0) return;
  const top = column[0].y;
  const last = column[column.length - 1];
  const blockCenter = (top + last.y + last.h) / 2;
  const shift = Math.max(center - blockCenter, TOP_MARGIN - top);
  if (shift === 0) return;
  for (const node of column) node.y += shift;
}

function makeRoute(
  kind: FlowRouteKind,
  from: FlowNode,
  to: FlowNode,
  eventTypeIds: string[],
): FlowRoute {
  return {
    id: `${kind}::${from.id}::${to.id}`,
    kind,
    fromNodeId: from.id,
    toNodeId: to.id,
    eventTypeIds,
    d: bezier(from, to),
  };
}

/**
 * Cubic bezier from the from-node's right-center anchor to the to-node's
 * left-center anchor — same shape language as topology-flow.tsx ribbons.
 * dx clamps the control-point pull to [40, 120]: enough curve for short hops
 * to read as flow, without long platform routes ballooning; the 40 floor also
 * gives right-to-left routes (consumer → Resolver topic) a readable S-curve
 * instead of a degenerate straight line. Coordinates round to one decimal so
 * path strings stay byte-stable across runs and engines.
 */
function bezier(from: FlowNode, to: FlowNode): string {
  const x1 = from.x + from.w;
  const y1 = from.y + from.h / 2;
  const x2 = to.x;
  const y2 = to.y + to.h / 2;
  const dx = Math.min(120, Math.max(40, (x2 - x1) / 2));
  return `M ${r1(x1)} ${r1(y1)} C ${r1(x1 + dx)} ${r1(y1)}, ${r1(x2 - dx)} ${r1(y2)}, ${r1(x2)} ${r1(y2)}`;
}

function r1(value: number): number {
  return Math.round(value * 10) / 10;
}

/**
 * Code-unit comparison instead of localeCompare: layout identity must not
 * depend on the runtime's collation tables (ICU builds differ between node
 * and browsers). Endpoint id is the final tiebreaker so ordering stays total
 * even with duplicate display names.
 */
function compareByName(a: TopologyNode, b: TopologyNode): number {
  if (a.name < b.name) return -1;
  if (a.name > b.name) return 1;
  if (a.id < b.id) return -1;
  if (a.id > b.id) return 1;
  return 0;
}

function compareStrings<T>(key: (item: T) => string): (a: T, b: T) => number {
  return (a, b) => {
    const ka = key(a);
    const kb = key(b);
    return ka < kb ? -1 : ka > kb ? 1 : 0;
  };
}
