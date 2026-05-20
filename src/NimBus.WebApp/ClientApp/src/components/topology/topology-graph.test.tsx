import { describe, it, expect, afterEach, vi } from "vitest";
import { render, screen, cleanup } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { TopologyGraph } from "./topology-graph";
import type { TopologyNode, TopologyEdge, EventPill } from "./types";

const nodes: TopologyNode[] = [
  {
    id: "EndpointA",
    name: "EndpointA",
    role: "Endpoint",
    publishCount: 2,
    subscribeCount: 1,
    publishedMessages: 100,
    handledMessages: 80,
    failedMessages: 0,
    health: "good",
  },
  {
    id: "EndpointB",
    name: "EndpointB",
    role: "Endpoint",
    publishCount: 0,
    subscribeCount: 3,
    publishedMessages: 0,
    handledMessages: 250,
    failedMessages: 5,
    health: "warn",
  },
];

const edges: TopologyEdge[] = [
  {
    id: "publish-EndpointA",
    kind: "publish",
    endpointId: "EndpointA",
    eventTypeIds: ["EventX"],
    messages: 100,
    health: "healthy",
  },
  {
    id: "subscribe-EndpointB",
    kind: "subscribe",
    endpointId: "EndpointB",
    eventTypeIds: ["EventX"],
    messages: 95,
    health: "warn",
  },
];

const pills: EventPill[] = [
  {
    id: "EventX",
    label: "EventX",
    anchorEndpointId: "EndpointA",
    kind: "publish",
    tooltip: "EndpointA → EndpointB · 100 / 1h",
  },
];

afterEach(() => cleanup());

describe("TopologyGraph (smoke)", () => {
  it("renders an SVG with both endpoint cards visible", () => {
    render(
      <TopologyGraph
        nodes={nodes}
        edges={edges}
        pills={pills}
        onSelectNode={() => {}}
      />,
    );

    // Endpoint names are rendered as plain text inside the SVG cards.
    expect(screen.getByText("EndpointA")).toBeDefined();
    expect(screen.getByText("EndpointB")).toBeDefined();
  });

  it("renders the event-type pill labels", () => {
    render(
      <TopologyGraph
        nodes={nodes}
        edges={edges}
        pills={pills}
        onSelectNode={() => {}}
      />,
    );

    expect(screen.getByText("EventX")).toBeDefined();
  });

  it("calls onSelectNode when a card is clicked", async () => {
    const onSelectNode = vi.fn();
    const user = userEvent.setup();
    render(
      <TopologyGraph
        nodes={nodes}
        edges={edges}
        pills={pills}
        onSelectNode={onSelectNode}
      />,
    );

    // Clicking the endpoint name text bubbles up to the card-level click
    // handler that the component installs on its SVG group.
    await user.click(screen.getByText("EndpointA"));

    expect(onSelectNode).toHaveBeenCalled();
    expect(onSelectNode.mock.calls[0][0]).toBe("EndpointA");
  });

  it("renders without crashing on an empty topology", () => {
    render(
      <TopologyGraph
        nodes={[]}
        edges={[]}
        pills={[]}
        onSelectNode={() => {}}
      />,
    );
    // No exception thrown is the contract; nothing specific to assert.
    expect(true).toBe(true);
  });
});
