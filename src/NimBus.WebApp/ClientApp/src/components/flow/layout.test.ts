import { describe, expect, it } from "vitest";
import type {
  FlowEdge,
  TopologyData,
  TopologyNode,
} from "components/topology/types";
import { buildFlowLayout, topEndpointIds } from "./layout";
import { TOP_N_DEFAULT } from "./types";

// Spec 020 — layout engine unit tests. buildFlowLayout is pure (no DOM, no
// clock, no randomness), so every assertion here pins exact geometry / ids;
// any drift in the layout contract should fail loudly rather than render
// subtly wrong.

/* Fixture builders — construct full minimal objects so strict mode stays
   happy without `as` casts. Only the fields layout reads get meaningful
   values; the rest are zeroed defaults. */

function endpoint(
  overrides: Partial<TopologyNode> & Pick<TopologyNode, "id">,
): TopologyNode {
  return {
    name: overrides.id,
    role: "Endpoint",
    publishCount: 0,
    subscribeCount: 0,
    publishedMessages: 0,
    handledMessages: 0,
    failedMessages: 0,
    health: "good",
    ...overrides,
  };
}

function flowEdge(from: string, to: string, eventTypeIds: string[]): FlowEdge {
  return {
    id: `${from}::${to}`,
    from,
    to,
    eventTypeIds,
    messages: 0,
    failures: 0,
    health: "live",
    tooltip: "",
  };
}

function topo(nodes: TopologyNode[], flowEdges: FlowEdge[] = []): TopologyData {
  return {
    nodes,
    edges: [],
    pills: [],
    flowEdges,
    summary: {
      endpoints: nodes.length,
      eventTypes: 0,
      edges: 0,
      edgesWithFailures: 0,
      namespaces: 0,
      producingEndpoints: 0,
      consumingEndpoints: 0,
    },
  };
}

/**
 * Canonical fixture:
 *   erp   — producer only, loudest (900 published)
 *   crm   — both roles (300 published / 500 handled)
 *   audit — consumer only, quiet (200 handled)
 * Routes: erp→crm (OrderPlaced), erp→audit (OrderPlaced, InvoiceRaised),
 *         crm→audit (CustomerRegistered).
 */
function canonical(): TopologyData {
  return topo(
    [
      endpoint({
        id: "erp",
        name: "ERP",
        publishCount: 2,
        publishedMessages: 900,
        health: "good",
      }),
      endpoint({
        id: "crm",
        name: "CRM",
        publishCount: 1,
        subscribeCount: 1,
        publishedMessages: 300,
        handledMessages: 500,
        health: "warn",
      }),
      endpoint({
        id: "audit",
        name: "Audit Log",
        subscribeCount: 2,
        handledMessages: 200,
        health: "bad",
      }),
    ],
    [
      flowEdge("erp", "crm", ["OrderPlaced"]),
      flowEdge("erp", "audit", ["OrderPlaced", "InvoiceRaised"]),
      flowEdge("crm", "audit", ["CustomerRegistered"]),
    ],
  );
}

describe("buildFlowLayout", () => {
  it("is deterministic: repeated and input-order-shuffled calls produce deep-equal layouts", () => {
    const first = buildFlowLayout(canonical());
    expect(buildFlowLayout(canonical())).toEqual(first);

    // Reversing the input arrays must not change anything — ordering comes
    // from traffic + name, never from incidental catalog order.
    const shuffled = canonical();
    shuffled.nodes.reverse();
    shuffled.flowEdges.reverse();
    expect(buildFlowLayout(shuffled)).toEqual(first);
  });

  it("places a both-role endpoint in both producer and consumer columns with distinct node ids", () => {
    const layout = buildFlowLayout(canonical());
    const producer = layout.nodes.find((n) => n.id === "producer::crm");
    const consumer = layout.nodes.find((n) => n.id === "consumer::crm");
    expect(producer).toBeDefined();
    expect(consumer).toBeDefined();
    expect(producer!.id).not.toBe(consumer!.id);
    expect(producer!.x).toBe(20);
    expect(consumer!.x).toBe(660);
    expect(producer!.endpointId).toBe("crm");
    expect(consumer!.endpointId).toBe("crm");
    // Health is copied from the TopologyNode onto both column instances.
    expect(producer!.health).toBe("warn");
    expect(consumer!.health).toBe("warn");
  });

  it("stacks each column top-down by descending traffic with margin 20 / gap 16", () => {
    const layout = buildFlowLayout(canonical());
    const byId = new Map(layout.nodes.map((n) => [n.id, n]));
    // Producers: erp (900) above crm (300).
    expect(byId.get("producer::erp")!.y).toBe(20);
    expect(byId.get("producer::crm")!.y).toBe(100); // 20 + 64 + 16
    // Consumers: crm (500 handled) above audit (200).
    expect(byId.get("consumer::crm")!.y).toBe(20);
    expect(byId.get("consumer::audit")!.y).toBe(100);
    // Topics mirror producer order, then the two platform chips at the bottom.
    expect(byId.get("topic::erp")!.y).toBe(20);
    expect(byId.get("topic::crm")!.y).toBe(76); // 20 + 40 + 16
    expect(byId.get("topic::__resolver")!.y).toBe(132);
    expect(byId.get("topic::__manager")!.y).toBe(188);
    // Platform column: Resolver Worker above Message Store.
    expect(byId.get("platform::resolver")!.y).toBe(20);
    expect(byId.get("platform::store")!.y).toBe(108); // 20 + 72 + 16
    // Canvas: fixed width; height clamps to the 600 minimum for small graphs.
    expect(layout.width).toBe(1240);
    expect(layout.height).toBe(600);
  });

  it("breaks equal-traffic ordering ties by name ascending", () => {
    const tied = topo([
      endpoint({ id: "z", name: "Zeta", publishCount: 1, publishedMessages: 100 }),
      endpoint({ id: "a", name: "Alpha", publishCount: 1, publishedMessages: 100 }),
    ]);
    const producers = buildFlowLayout(tied).nodes.filter(
      (n) => n.kind === "producer",
    );
    expect(producers.map((n) => n.title)).toEqual(["Alpha", "Zeta"]);
  });

  it("emits routes that reference existing nodes and follow the kind::from::to id scheme", () => {
    const layout = buildFlowLayout(canonical());
    const nodeIds = new Set(layout.nodes.map((n) => n.id));
    for (const route of layout.routes) {
      expect(nodeIds.has(route.fromNodeId)).toBe(true);
      expect(nodeIds.has(route.toNodeId)).toBe(true);
      expect(route.id).toBe(`${route.kind}::${route.fromNodeId}::${route.toNodeId}`);
    }
    const routeIds = layout.routes.map((r) => r.id);
    expect(routeIds).toContain("publish::producer::erp::topic::erp");
    expect(routeIds).toContain("deliver::topic::erp::consumer::audit");
    expect(routeIds).toContain("outcome::consumer::audit::topic::__resolver");
    expect(routeIds).toContain("outcome::topic::__resolver::platform::resolver");
    expect(routeIds).toContain("outcome::platform::resolver::platform::store");
  });

  it("unions + sorts publish-route event types from flow edges; deliver routes carry the edge's list verbatim", () => {
    const layout = buildFlowLayout(canonical());
    const publish = layout.routes.find(
      (r) => r.id === "publish::producer::erp::topic::erp",
    );
    expect(publish!.eventTypeIds).toEqual(["InvoiceRaised", "OrderPlaced"]);
    const deliver = layout.routes.find(
      (r) => r.id === "deliver::topic::erp::consumer::audit",
    );
    expect(deliver!.eventTypeIds).toEqual(["OrderPlaced", "InvoiceRaised"]);
  });

  it("renders cubic beziers between right-center and left-center anchors, rounded to one decimal", () => {
    const layout = buildFlowLayout(
      topo([endpoint({ id: "erp", name: "ERP", publishCount: 1, publishedMessages: 10 })]),
    );
    const publish = layout.routes.find((r) => r.kind === "publish");
    // producer::erp anchor (20+220, 20+32) = (240, 52); topic::erp anchor
    // (330, 20+20) = (330, 40); dx = clamp((330−240)/2, 40, 120) = 45.
    expect(publish!.d).toBe("M 240 52 C 285 52, 285 40, 330 40");
  });

  it("produces only finite, non-negative geometry and NaN-free paths", () => {
    const layout = buildFlowLayout(canonical());
    for (const n of layout.nodes) {
      for (const value of [n.x, n.y, n.w, n.h]) {
        expect(Number.isFinite(value)).toBe(true);
        expect(value).toBeGreaterThanOrEqual(0);
      }
    }
    for (const route of layout.routes) {
      expect(route.d).not.toMatch(/NaN/);
    }
    expect(Number.isFinite(layout.width)).toBe(true);
    expect(Number.isFinite(layout.height)).toBe(true);
  });

  it("indexes every consumer in byEndpoint with sorted, existing route ids", () => {
    const layout = buildFlowLayout(canonical());
    const nodeIds = new Set(layout.nodes.map((n) => n.id));
    const routeIds = new Set(layout.routes.map((r) => r.id));
    expect(Object.keys(layout.byEndpoint).sort()).toEqual(["audit", "crm"]);
    for (const [endpointId, index] of Object.entries(layout.byEndpoint)) {
      expect(index.nodeId).toBe(`consumer::${endpointId}`);
      expect(nodeIds.has(index.nodeId)).toBe(true);
      expect(routeIds.has(index.outcome)).toBe(true);
      for (const id of index.deliver) {
        expect(routeIds.has(id)).toBe(true);
      }
      expect([...index.deliver].sort()).toEqual(index.deliver);
    }
    // audit fans in from both producers; sorted ids put crm before erp.
    expect(layout.byEndpoint["audit"].deliver).toEqual([
      "deliver::topic::crm::consumer::audit",
      "deliver::topic::erp::consumer::audit",
    ]);
  });

  it("filters hidden endpoints out of every column but always keeps platform fixtures", () => {
    const layout = buildFlowLayout(canonical(), {
      visibleEndpointIds: new Set(["erp", "audit"]),
    });
    const nodeIds = new Set(layout.nodes.map((n) => n.id));
    expect(nodeIds.has("producer::crm")).toBe(false);
    expect(nodeIds.has("consumer::crm")).toBe(false);
    expect(nodeIds.has("topic::crm")).toBe(false);
    for (const id of [
      "topic::__resolver",
      "topic::__manager",
      "platform::resolver",
      "platform::store",
    ]) {
      expect(nodeIds.has(id)).toBe(true);
    }
    const routeIds = layout.routes.map((r) => r.id);
    expect(routeIds).toContain("deliver::topic::erp::consumer::audit");
    // crm is hidden → its inbound (erp→crm) and outbound (crm→audit) deliver
    // routes are dropped silently.
    expect(routeIds).not.toContain("deliver::topic::erp::consumer::crm");
    expect(routeIds).not.toContain("deliver::topic::crm::consumer::audit");
    expect(Object.keys(layout.byEndpoint)).toEqual(["audit"]);
  });

  it("drops deliver routes when the consumer end is hidden but keeps the producer's publish route", () => {
    const layout = buildFlowLayout(canonical(), {
      visibleEndpointIds: new Set(["erp"]),
    });
    expect(layout.routes.filter((r) => r.kind === "deliver")).toHaveLength(0);
    expect(
      layout.routes.find((r) => r.id === "publish::producer::erp::topic::erp"),
    ).toBeDefined();
    expect(layout.byEndpoint).toEqual({});
  });

  it("renders a platform-only layout for empty topology data without throwing", () => {
    const layout = buildFlowLayout(topo([]));
    expect(layout.nodes.map((n) => n.id).sort()).toEqual([
      "platform::resolver",
      "platform::store",
      "topic::__manager",
      "topic::__resolver",
    ]);
    expect(layout.routes.map((r) => r.id)).toEqual([
      "outcome::topic::__resolver::platform::resolver",
      "outcome::platform::resolver::platform::store",
    ]);
    expect(layout.byEndpoint).toEqual({});
    expect(layout.width).toBe(1240);
    expect(layout.height).toBe(600);
  });

  it("labels platform chips and platform nodes per spec", () => {
    const layout = buildFlowLayout(topo([]));
    const byId = new Map(layout.nodes.map((n) => [n.id, n]));
    expect(byId.get("topic::__resolver")).toMatchObject({
      kind: "topic",
      title: "Resolver",
      subtitle: "outcome stream",
      h: 40,
      health: "good",
    });
    expect(byId.get("topic::__manager")).toMatchObject({
      kind: "topic",
      title: "Manager",
      subtitle: "recovery commands",
      h: 40,
    });
    expect(byId.get("platform::resolver")).toMatchObject({
      kind: "platform",
      title: "Resolver Worker",
      h: 72,
      health: "good",
    });
    expect(byId.get("platform::store")).toMatchObject({
      kind: "platform",
      title: "Message Store",
      h: 72,
    });
  });

  it("describes endpoint roles in subtitles and titles topics by endpoint id", () => {
    const layout = buildFlowLayout(canonical());
    const byId = new Map(layout.nodes.map((n) => [n.id, n]));
    expect(byId.get("producer::erp")!.title).toBe("ERP");
    expect(byId.get("producer::erp")!.subtitle).toBe("publishes 2 event type(s)");
    expect(byId.get("consumer::audit")!.subtitle).toBe("handles 2 event type(s)");
    // Topic chips carry the endpoint id (one topic per endpoint), not the
    // display name.
    expect(byId.get("topic::erp")!.title).toBe("erp");
  });
});

describe("topEndpointIds", () => {
  it("ranks by published + handled traffic, descending", () => {
    const data = topo([
      endpoint({ id: "low", name: "Low", publishedMessages: 5, handledMessages: 5 }),
      endpoint({ id: "high", name: "High", publishedMessages: 100, handledMessages: 150 }),
      endpoint({ id: "mid", name: "Mid", handledMessages: 60 }),
    ]);
    expect(topEndpointIds(data, 2)).toEqual(["high", "mid"]);
    expect(topEndpointIds(data, 10)).toEqual(["high", "mid", "low"]);
  });

  it("breaks ties by name ascending", () => {
    const data = topo([
      endpoint({ id: "b", name: "Beta", publishedMessages: 50, handledMessages: 50 }),
      endpoint({ id: "a", name: "Alpha", publishedMessages: 100 }),
      endpoint({ id: "g", name: "Gamma", handledMessages: 100 }),
    ]);
    expect(topEndpointIds(data, 3)).toEqual(["a", "b", "g"]);
  });

  it("returns an empty list for n <= 0 and caps at the catalog size", () => {
    const data = topo([endpoint({ id: "only" })]);
    expect(topEndpointIds(data, 0)).toEqual([]);
    expect(topEndpointIds(data, -3)).toEqual([]);
    expect(topEndpointIds(data, TOP_N_DEFAULT)).toEqual(["only"]);
  });
});
