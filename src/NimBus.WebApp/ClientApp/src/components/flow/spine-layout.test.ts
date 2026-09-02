import { describe, expect, it } from "vitest";
import type { TopologyData } from "components/topology/types";
import {
  buildSpineModel,
  computeSpineEdges,
  formatRate,
  laneWidth,
  spineFocusLine,
} from "./spine-layout";
import type { SpineLane, SpineRect } from "./spine-layout";

// Lanes view (design 1b) — spine model + edge math unit tests. Both functions
// are pure, so every assertion pins exact numbers/paths; drift in the lane
// contract should fail loudly rather than render subtly wrong.

function topo(spine: TopologyData["spine"]): TopologyData {
  return {
    nodes: [],
    edges: [],
    pills: [],
    flowEdges: [],
    spine,
    summary: {
      endpoints: 0,
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
 * Canonical fixture (window = 60 minutes):
 *   Crm  publishes OrderCreated (600) and ShipmentBooked (120)
 *   Erp  publishes OrderCreated (0 — idle hop) and handles it (540, 60 failed)
 *   Audit handles OrderCreated (60) and ShipmentBooked (120)
 */
const FIXTURE = topo({
  types: [
    {
      id: "OrderCreated",
      label: "OrderCreated",
      namespace: "Sales",
      producers: 2,
      consumers: 2,
      published: 600,
      handled: 600,
      failed: 60,
    },
    {
      id: "ShipmentBooked",
      label: "ShipmentBooked",
      namespace: "Logistics",
      producers: 1,
      consumers: 1,
      published: 120,
      handled: 120,
      failed: 0,
    },
  ],
  links: [
    { id: "pub::Crm::OrderCreated", kind: "pub", endpointId: "Crm", eventTypeId: "OrderCreated", messages: 600, failures: 0 },
    { id: "pub::Crm::ShipmentBooked", kind: "pub", endpointId: "Crm", eventTypeId: "ShipmentBooked", messages: 120, failures: 0 },
    { id: "pub::Erp::OrderCreated", kind: "pub", endpointId: "Erp", eventTypeId: "OrderCreated", messages: 0, failures: 0 },
    { id: "sub::Audit::OrderCreated", kind: "sub", endpointId: "Audit", eventTypeId: "OrderCreated", messages: 60, failures: 0 },
    { id: "sub::Audit::ShipmentBooked", kind: "sub", endpointId: "Audit", eventTypeId: "ShipmentBooked", messages: 120, failures: 0 },
    { id: "sub::Erp::OrderCreated", kind: "sub", endpointId: "Erp", eventTypeId: "OrderCreated", messages: 540, failures: 60 },
  ],
});

describe("buildSpineModel", () => {
  it("derives columns, per-minute rates, and lane healths", () => {
    const model = buildSpineModel(FIXTURE, { periodMinutes: 60 });

    expect(model.publishers.map((p) => p.key)).toEqual(["p:Crm", "p:Erp"]);
    expect(model.publishers[0]).toMatchObject({ rate: 12, typeCount: 2 });
    expect(model.publishers[1]).toMatchObject({ rate: 0, typeCount: 1 });

    expect(model.subscribers.map((s) => s.key)).toEqual(["s:Erp", "s:Audit"]);
    expect(model.subscribers[0]).toMatchObject({ rate: 9, failed: 60 });
    expect(model.subscribers[1]).toMatchObject({ rate: 3, failed: 0 });

    expect(model.types.map((t) => t.key)).toEqual([
      "t:OrderCreated",
      "t:ShipmentBooked",
    ]);
    expect(model.totalRate).toBe(12);

    const byKey = new Map(model.lanes.map((l) => [l.key, l]));
    expect(byKey.get("pub::Erp::OrderCreated")?.health).toBe("idle");
    expect(byKey.get("sub::Erp::OrderCreated")?.health).toBe("fail");
    expect(byKey.get("pub::Crm::OrderCreated")).toMatchObject({
      s: "p:Crm",
      t: "t:OrderCreated",
      rate: 10,
      health: "ok",
    });
  });

  it("keeps the data layer's type order (grouped by namespace)", () => {
    // FIXTURE.types arrive Sales-first; the model must not re-sort them —
    // namespace grouping is the data layer's contract, not this module's.
    const model = buildSpineModel(FIXTURE, { periodMinutes: 60 });
    expect(model.types.map((t) => t.namespace)).toEqual([
      "Sales",
      "Logistics",
    ]);
  });

  it("narrows to one event type end to end", () => {
    const model = buildSpineModel(FIXTURE, {
      periodMinutes: 60,
      eventType: "OrderCreated",
    });
    expect(model.types.map((t) => t.eventTypeId)).toEqual(["OrderCreated"]);
    expect(model.lanes.every((l) => l.key.endsWith("::OrderCreated"))).toBe(
      true,
    );
    // Crm keeps only its OrderCreated hop on the narrowed spine.
    expect(model.publishers.find((p) => p.endpointId === "Crm")).toMatchObject({
      rate: 10,
      typeCount: 1,
    });
  });

  it("drops hidden endpoints' hops and types left with no hops", () => {
    const model = buildSpineModel(FIXTURE, {
      periodMinutes: 60,
      visibleEndpointIds: new Set(["Crm", "Erp"]),
    });
    expect(model.subscribers.map((s) => s.endpointId)).toEqual(["Erp"]);
    // ShipmentBooked keeps its publish hop, so the type survives.
    expect(model.types.map((t) => t.eventTypeId)).toEqual([
      "OrderCreated",
      "ShipmentBooked",
    ]);
    expect(
      model.lanes.some((l) => l.key.includes("Audit")),
    ).toBe(false);
  });
});

describe("type truncation (maxTypes)", () => {
  // A busy (1000), B a low-rate failing trickle (10, 5 failed), C mid (500),
  // D idle. Selection ranks by traffic; display keeps the given (namespace-
  // grouped) order; failing types always survive the cut.
  const CATALOG = topo({
    types: [
      { id: "A", label: "A", namespace: "N1", producers: 1, consumers: 0, published: 1000, handled: 0, failed: 0 },
      { id: "B", label: "B", namespace: "N1", producers: 1, consumers: 1, published: 10, handled: 10, failed: 5 },
      { id: "C", label: "C", namespace: "N2", producers: 1, consumers: 0, published: 500, handled: 0, failed: 0 },
      { id: "D", label: "D", namespace: "N2", producers: 1, consumers: 0, published: 0, handled: 0, failed: 0 },
    ],
    links: [
      { id: "pub::P::A", kind: "pub", endpointId: "P", eventTypeId: "A", messages: 1000, failures: 0 },
      { id: "pub::P::B", kind: "pub", endpointId: "P", eventTypeId: "B", messages: 10, failures: 0 },
      { id: "pub::P::C", kind: "pub", endpointId: "P", eventTypeId: "C", messages: 500, failures: 0 },
      { id: "pub::P::D", kind: "pub", endpointId: "P", eventTypeId: "D", messages: 0, failures: 0 },
      { id: "sub::S::B", kind: "sub", endpointId: "S", eventTypeId: "B", messages: 10, failures: 5 },
    ],
  });

  it("keeps the busiest types plus every failing type, drops idle, preserves order", () => {
    const model = buildSpineModel(CATALOG, { periodMinutes: 60, maxTypes: 2 });
    // Top-2 by traffic = A, C; B rides the failure override; D is idle.
    expect(model.types.map((t) => t.eventTypeId)).toEqual(["A", "B", "C"]);
    expect(model.totalTypeCount).toBe(4);
    // Lanes for the dropped type disappear with it.
    expect(model.lanes.some((l) => l.key === "pub::P::D")).toBe(false);
  });

  it("shows everything, idle included, without a cap", () => {
    const model = buildSpineModel(CATALOG, { periodMinutes: 60 });
    expect(model.types.map((t) => t.eventTypeId)).toEqual(["A", "B", "C", "D"]);
    expect(model.totalTypeCount).toBe(4);
  });

  it("an explicit event-type filter bypasses the cap", () => {
    const model = buildSpineModel(CATALOG, {
      periodMinutes: 60,
      maxTypes: 2,
      eventType: "D",
    });
    expect(model.types.map((t) => t.eventTypeId)).toEqual(["D"]);
    expect(model.totalTypeCount).toBe(1);
  });
});

describe("edge math", () => {
  const RECTS: Record<string, SpineRect> = {
    "p:a": { x: 0, y: 0, w: 100, h: 60 },
    "p:b": { x: 0, y: 100, w: 100, h: 60 },
    "t:x": { x: 300, y: 40, w: 120, h: 60 },
  };
  const LANES: SpineLane[] = [
    { key: "l1", s: "p:a", t: "t:x", rate: 100, health: "ok" },
    { key: "l2", s: "p:b", t: "t:x", rate: 0, health: "idle" },
  ];

  it("distributes ports along the shared card, sorted by peer position", () => {
    const { edges } = computeSpineEdges(RECTS, LANES, { focus: null });
    // Two lanes enter t:x — ports land at h·(1/3) and h·(2/3), peer-sorted.
    expect(edges[0].d).toBe("M 100 30 C 190 30, 210 60, 300 60");
    expect(edges[1].d).toBe("M 100 130 C 190 130, 210 80, 300 80");
    expect(edges[0].color).toBe("#2E8F5E");
    expect(edges[1].color).toBe("#C9C1AB");
    expect(edges.every((e) => e.op === 0.42)).toBe(true);
  });

  it("isolates the hovered node's lanes and rings both ends", () => {
    const { edges, rings } = computeSpineEdges(RECTS, LANES, { focus: "p:a" });
    expect(edges[0].op).toBe(0.95);
    expect(edges[1].op).toBeCloseTo(0.42 * 0.16, 10);
    expect(edges[0].dop).toBe(1);
    expect(edges[1].dop).toBe(0);
    expect(rings.map((r) => r.key)).toEqual(["p:a", "t:x"]);
    expect(rings[0]).toMatchObject({ x: -4, y: -4, w: 108, h: 68 });
  });

  it("removes the marching-dash overlay when animation is off", () => {
    const { edges } = computeSpineEdges(RECTS, LANES, {
      focus: null,
      animate: false,
    });
    expect(edges.every((e) => e.dop === 0)).toBe(true);
  });

  it("skips lanes whose rects are not measured yet", () => {
    const { edges } = computeSpineEdges({ "p:a": RECTS["p:a"] }, LANES, {
      focus: null,
    });
    expect(edges).toEqual([]);
  });

  it("weights lanes on a capped square-root scale", () => {
    expect(laneWidth(0)).toBeCloseTo(1.1, 10);
    expect(laneWidth(100)).toBeCloseTo(1.1 + 10 / 5.2, 10);
    expect(laneWidth(1_000_000)).toBe(10);
  });
});

describe("spineFocusLine", () => {
  const model = buildSpineModel(FIXTURE, { periodMinutes: 60 });

  it("narrates a hovered event type with both hops and the failing rate", () => {
    expect(spineFocusLine(model, "t:OrderCreated")).toBe(
      "OrderCreated · receives 10/min from 2 publishers · delivers 10/min to 2 subscribers · 9/min failing",
    );
  });

  it("narrates a hovered publisher", () => {
    expect(spineFocusLine(model, "p:Crm")).toBe(
      "Crm · publishes 12/min across 2 types",
    );
  });

  it("narrates a hovered subscriber", () => {
    expect(spineFocusLine(model, "s:Audit")).toBe(
      "Audit · receives 3/min from 2 types",
    );
  });

  it("is empty without a focus", () => {
    expect(spineFocusLine(model, null)).toBe("");
  });
});

describe("formatRate", () => {
  it("formats zero, trickles, workhorse rates, and thousands", () => {
    expect(formatRate(0)).toBe("0");
    expect(formatRate(0.04)).toBe("0.1");
    expect(formatRate(7.44)).toBe("7.4");
    expect(formatRate(23.6)).toBe("24");
    expect(formatRate(1150)).toBe("1.15k");
    expect(formatRate(1500)).toBe("1.5k");
  });
});
