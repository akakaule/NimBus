import { cleanup, render } from "@testing-library/react";
import { afterEach, expect, it, vi } from "vitest";
import { MemoryRouter } from "react-router-dom";
import * as api from "api-client";
import type { TopologyData } from "components/topology/types";
import { SpineView } from "./spine-view";

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
});

const topology: TopologyData = {
  nodes: [],
  edges: [],
  pills: [],
  flowEdges: [],
  summary: {
    endpoints: 2,
    eventTypes: 1,
    edges: 1,
    edgesWithFailures: 0,
    namespaces: 1,
    producingEndpoints: 1,
    consumingEndpoints: 1,
  },
  spine: {
    types: [
      {
        id: "Created",
        label: "Created",
        namespace: "Demo",
        producers: 1,
        consumers: 1,
        published: 1,
        handled: 1,
        failed: 0,
      },
    ],
    links: [
      {
        id: "publish",
        kind: "pub",
        endpointId: "crm",
        eventTypeId: "Created",
        messages: 1,
        failures: 0,
      },
      {
        id: "deliver",
        kind: "sub",
        endpointId: "erp",
        eventTypeId: "Created",
        messages: 1,
        failures: 0,
      },
    ],
  },
};
const delivery = new api.Message({
  messageId: "delivery",
  from: "crm",
  to: "erp",
  eventTypeId: "Created",
  messageType: api.MessageType.EventRequest,
});

function setup(reducedMotion = false) {
  vi.stubGlobal("matchMedia", () => ({ matches: reducedMotion }));
  vi.spyOn(HTMLElement.prototype, "getBoundingClientRect").mockImplementation(
    function (this: HTMLElement) {
      const x = this.dataset.node?.startsWith("t:")
        ? 400
        : this.dataset.node?.startsWith("s:")
          ? 800
          : 0;
      return {
        x,
        y: 0,
        left: x,
        top: 0,
        width: 200,
        height: 60,
        right: x + 200,
        bottom: 60,
        toJSON: () => ({}),
      };
    },
  );
}

it("animates both route segments only for an attributed delivery and clears them on pause", () => {
  setup();
  const view = (traffic: api.Message[]) => (
    <MemoryRouter>
      <SpineView
        topology={topology}
        eventType=""
        periodMinutes={60}
        periodLabel="1h"
        snapshots={{}}
        traffic={traffic}
      />
    </MemoryRouter>
  );
  const { container, rerender } = render(
    view([
      delivery,
      new api.Message({
        ...delivery,
        messageId: "response",
        messageType: api.MessageType.ResolutionResponse,
      }),
      new api.Message({ ...delivery, messageId: "unknown", from: "unknown" }),
    ]),
  );
  expect(container.querySelectorAll("[data-message-traffic]")).toHaveLength(1);
  const dots = container.querySelectorAll("circle");
  expect(dots).toHaveLength(2);
  expect(dots[0].style.offsetPath).toMatch(/M\s*200/);
  expect(dots[1].style.offsetPath).toMatch(/M\s*600/);
  expect(dots[1].style.animation).toContain("1.5s forwards");
  rerender(view([]));
  expect(container.querySelectorAll("circle")).toHaveLength(0);
});

it("uses a static route highlight for reduced motion", () => {
  setup(true);
  const { container } = render(
    <MemoryRouter>
      <SpineView
        topology={topology}
        eventType=""
        periodMinutes={60}
        periodLabel="1h"
        snapshots={{}}
        traffic={[delivery]}
      />
    </MemoryRouter>,
  );
  expect(container.querySelectorAll("circle")).toHaveLength(0);
  expect(
    container.querySelectorAll("[data-message-traffic] path"),
  ).toHaveLength(1);
});
