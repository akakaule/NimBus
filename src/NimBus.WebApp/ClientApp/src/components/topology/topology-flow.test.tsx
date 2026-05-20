import { describe, it, expect, afterEach, vi } from "vitest";
import { render, screen, cleanup } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { TopologyFlow } from "./topology-flow";
import type { FlowEdge, TopologyNode } from "./types";

const nodes: TopologyNode[] = [
  {
    id: "Crm",
    name: "Crm",
    role: "Endpoint",
    publishCount: 2,
    subscribeCount: 1,
    publishedMessages: 100,
    handledMessages: 0,
    failedMessages: 0,
    health: "good",
  },
  {
    id: "Erp",
    name: "Erp",
    role: "Endpoint",
    publishCount: 0,
    subscribeCount: 2,
    publishedMessages: 0,
    handledMessages: 80,
    failedMessages: 4,
    health: "bad",
  },
  {
    id: "Analytics",
    name: "Analytics",
    role: "Endpoint",
    publishCount: 0,
    subscribeCount: 1,
    publishedMessages: 0,
    handledMessages: 40,
    failedMessages: 0,
    health: "good",
  },
];

const flowEdges: FlowEdge[] = [
  {
    id: "Crm::Erp",
    from: "Crm",
    to: "Erp",
    eventTypeIds: ["EvtA"],
    messages: 80,
    failures: 4,
    health: "fail",
    tooltip: "Crm → Erp · 80 ok · 4 failed",
  },
  {
    id: "Crm::Analytics",
    from: "Crm",
    to: "Analytics",
    eventTypeIds: ["EvtB"],
    messages: 40,
    failures: 0,
    health: "live",
    tooltip: "Crm → Analytics · 40 ok",
  },
];

afterEach(() => cleanup());

describe("TopologyFlow (smoke)", () => {
  it("renders publisher cards on the left and subscriber cards on the right", () => {
    render(
      <TopologyFlow
        nodes={nodes}
        flowEdges={flowEdges}
        onSelectNode={() => {}}
      />,
    );

    // Crm appears as a publisher; Erp and Analytics as subscribers.
    expect(screen.getByText("Crm")).toBeDefined();
    expect(screen.getByText("Erp")).toBeDefined();
    expect(screen.getByText("Analytics")).toBeDefined();
    // Axis labels are rendered as SVG <text>.
    expect(screen.getByText("PUBLISHERS · sends events")).toBeDefined();
    expect(screen.getByText("SUBSCRIBERS · receives events")).toBeDefined();
  });

  it("calls onSelectNode when an endpoint card is clicked", async () => {
    const onSelectNode = vi.fn();
    const user = userEvent.setup();
    render(
      <TopologyFlow
        nodes={nodes}
        flowEdges={flowEdges}
        onSelectNode={onSelectNode}
      />,
    );

    await user.click(screen.getByText("Crm"));

    expect(onSelectNode).toHaveBeenCalled();
    expect(onSelectNode.mock.calls[0][0]).toBe("Crm");
  });

  it("renders the empty-state copy when there are no flow edges", () => {
    render(
      <TopologyFlow
        nodes={[]}
        flowEdges={[]}
        onSelectNode={() => {}}
      />,
    );

    expect(
      screen.getByText(/No producer → consumer routes match/i),
    ).toBeDefined();
  });

  it("surfaces a failing-routes callout when at least one edge has failures", () => {
    render(
      <TopologyFlow
        nodes={nodes}
        flowEdges={flowEdges}
        onSelectNode={() => {}}
      />,
    );

    expect(screen.getByText(/failing route/i)).toBeDefined();
  });
});
