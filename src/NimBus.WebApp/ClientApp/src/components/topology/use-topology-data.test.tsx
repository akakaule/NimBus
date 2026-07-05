import { renderHook, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import * as api from "api-client";
import { useTopologyData } from "./use-topology-data";

// Spy that must never fire once the hook builds its detail map straight from
// the catalog response instead of fanning out a per-event-type detail call.
const detailByIdSpy = vi.fn();

// Catalog rows carrying the producer/consumer arrays inline — exactly what the
// server already returns from GET /event-types. The row without an `id` must be
// dropped by the hook.
const CATALOG = [
  {
    id: "order.created",
    name: "OrderCreated",
    namespace: "Sales",
    producers: ["OrderSvc"],
    consumers: ["BillingSvc", "ShippingSvc"],
  },
  {
    id: "invoice.paid",
    name: "InvoicePaid",
    namespace: "Finance",
    producers: ["BillingSvc"],
    consumers: ["LedgerSvc"],
  },
  {
    // No id → filtered out before it can produce nodes/edges.
    name: "Orphan",
    producers: ["Ghost"],
    consumers: ["Ghost"],
  },
];

vi.mock("api-client", () => {
  class Client {
    getEventTypes() {
      return Promise.resolve(CATALOG);
    }
    getEventtypesEventtypeid(id: string) {
      detailByIdSpy(id);
      return Promise.resolve({ producers: [], consumers: [] });
    }
    getMetricsOverview() {
      return Promise.resolve({ published: [], handled: [], failed: [] });
    }
  }
  class EventTypeDetails {
    producers?: string[];
    consumers?: string[];
    constructor(data?: { producers?: string[]; consumers?: string[] }) {
      Object.assign(this, data);
    }
  }
  return {
    Client,
    EventTypeDetails,
    CookieAuth: () => ({}),
    Period: { _1h: "1h" },
  };
});

describe("useTopologyData catalog-embedded producers/consumers", () => {
  beforeEach(() => {
    detailByIdSpy.mockClear();
  });

  afterEach(() => {
    vi.clearAllMocks();
  });

  it("builds topology from the catalog without any per-event-type detail fetch", async () => {
    const { result } = renderHook(() =>
      useTopologyData({ period: api.Period._1h }),
    );

    await waitFor(() => expect(result.current.data).toBeDefined());

    // (a) The N+1 fan-out is gone — the per-id endpoint is never touched.
    expect(detailByIdSpy).not.toHaveBeenCalled();

    const data = result.current.data!;

    // (b) Nodes/edges are derived from the embedded producer/consumer arrays.
    const nodeIds = data.nodes.map((n) => n.id).sort();
    expect(nodeIds).toEqual([
      "BillingSvc",
      "LedgerSvc",
      "OrderSvc",
      "ShippingSvc",
    ]);
    // The id-less catalog row must not leak a "Ghost" node.
    expect(nodeIds).not.toContain("Ghost");

    // A publish edge for OrderSvc carrying order.created.
    const publishOrder = data.edges.find(
      (e) => e.kind === "publish" && e.endpointId === "OrderSvc",
    );
    expect(publishOrder?.eventTypeIds).toContain("order.created");

    // Subscribe edges for both order.created consumers.
    const subscribers = data.edges
      .filter((e) => e.kind === "subscribe")
      .map((e) => e.endpointId)
      .sort();
    expect(subscribers).toEqual(["BillingSvc", "LedgerSvc", "ShippingSvc"]);

    // Bipartite flow routes resolve the producer→consumer pairs directly.
    const flowRoutes = data.flowEdges.map((f) => `${f.from}->${f.to}`).sort();
    expect(flowRoutes).toEqual([
      "BillingSvc->LedgerSvc",
      "OrderSvc->BillingSvc",
      "OrderSvc->ShippingSvc",
    ]);
  });
});
