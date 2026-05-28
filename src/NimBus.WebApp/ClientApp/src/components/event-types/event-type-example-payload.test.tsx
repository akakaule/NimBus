import {
  describe,
  it,
  expect,
  vi,
  beforeEach,
  afterEach,
} from "vitest";
import { render, screen, cleanup, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import EventTypeExamplePayload from "./event-type-example-payload";
import * as api from "api-client";

// Toast is mounted from a provider in the real app; stub it so the component
// under test can call addToast without bringing the provider into scope.
const addToast = vi.fn();
vi.mock("components/ui/toast", () => ({
  useToast: () => ({ addToast }),
}));

// The api-client module is the call surface the Generate-fake-data button
// invokes. We replace the Client class so we can drive the response.
// Note: vi.mock is hoisted; using vi.hoisted lets us share the mock fn
// across the factory body and the test assertions.
const { getEventtypesEventtypeidFakeMock } = vi.hoisted(() => ({
  getEventtypesEventtypeidFakeMock: vi.fn(),
}));
vi.mock("api-client", async () => {
  const actual = await vi.importActual<typeof import("api-client")>("api-client");
  class MockClient {
    getEventtypesEventtypeidFake = getEventtypesEventtypeidFakeMock;
  }
  return {
    ...actual,
    Client: MockClient,
    CookieAuth: () => ({}),
  };
});

function buildEventType(overrides: Partial<api.EventType> = {}): api.EventType {
  // The component reads .id / .name / .properties. We hand-roll the shape so
  // the test does not depend on the full nswag class graph.
  return {
    id: "TestEvent",
    name: "TestEvent",
    namespace: "Test.Events",
    description: "",
    properties: [
      { name: "CustomerId", typeName: "Guid" } as api.EventTypeProperty,
    ],
    ...overrides,
  } as api.EventType;
}

const SUCCESS_PAYLOAD = '{\n  "CustomerId": "00000000-0000-0000-0000-000000000123"\n}';

async function openModalAndClickGenerate() {
  const user = userEvent.setup();
  await user.click(screen.getByRole("button", { name: /Compose Event/ }));
  // The Generate-fake-data button only exists inside the modal.
  await user.click(screen.getByRole("button", { name: /Generate fake data/ }));
  return user;
}

describe("EventTypeExamplePayload — Generate fake data wiring", () => {
  beforeEach(() => {
    addToast.mockClear();
    getEventtypesEventtypeidFakeMock.mockReset();
  });

  afterEach(() => {
    cleanup();
  });

  it("populates the textarea when the API returns a payload", async () => {
    getEventtypesEventtypeidFakeMock.mockResolvedValue({
      payload: SUCCESS_PAYLOAD,
    });

    render(<EventTypeExamplePayload eventType={buildEventType()} />);
    await openModalAndClickGenerate();

    await waitFor(() => {
      expect(getEventtypesEventtypeidFakeMock).toHaveBeenCalledWith("TestEvent");
    });

    // The textarea should now hold the returned payload string verbatim.
    const textarea = screen.getByRole("textbox") as HTMLTextAreaElement;
    await waitFor(() => {
      expect(textarea.value).toBe(SUCCESS_PAYLOAD);
    });

    // No error toast on the happy path.
    expect(addToast).not.toHaveBeenCalled();
  });

  it("shows a non-blocking toast and leaves the textarea unchanged when payload is null", async () => {
    getEventtypesEventtypeidFakeMock.mockResolvedValue({ payload: null });

    render(<EventTypeExamplePayload eventType={buildEventType()} />);
    const user = userEvent.setup();
    await user.click(screen.getByRole("button", { name: /Compose Event/ }));
    const textarea = screen.getByRole("textbox") as HTMLTextAreaElement;
    const before = textarea.value;
    await user.click(screen.getByRole("button", { name: /Generate fake data/ }));

    await waitFor(() => {
      expect(addToast).toHaveBeenCalledTimes(1);
    });
    expect(addToast.mock.calls[0]?.[0]).toMatchObject({
      title: expect.stringContaining("could not be generated"),
    });
    expect(textarea.value).toBe(before);
  });

  it("shows an error toast and leaves the textarea unchanged when the call fails (5xx / network)", async () => {
    getEventtypesEventtypeidFakeMock.mockRejectedValue(new Error("network blew up"));

    render(<EventTypeExamplePayload eventType={buildEventType()} />);
    const user = userEvent.setup();
    await user.click(screen.getByRole("button", { name: /Compose Event/ }));
    const textarea = screen.getByRole("textbox") as HTMLTextAreaElement;
    const before = textarea.value;
    await user.click(screen.getByRole("button", { name: /Generate fake data/ }));

    await waitFor(() => {
      expect(addToast).toHaveBeenCalledTimes(1);
    });
    expect(addToast.mock.calls[0]?.[0]).toMatchObject({
      title: expect.stringContaining("Could not generate fake data"),
      variant: "error",
    });
    expect(textarea.value).toBe(before);
  });
});
