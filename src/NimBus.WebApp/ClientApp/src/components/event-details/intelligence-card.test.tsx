import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import IntelligenceCard from "./intelligence-card";

const originalFetch = globalThis.fetch;

afterEach(() => {
  cleanup();
  globalThis.fetch = originalFetch;
});

describe("IntelligenceCard", () => {
  it("hides unsupported contract versions without loading or posting", async () => {
    const fetchMock = vi.fn().mockResolvedValue(new Response(JSON.stringify({ status: "Ready", canAnalyze: true, contractVersion: 99 })));
    globalThis.fetch = fetchMock as typeof fetch;
    render(<IntelligenceCard endpointId="orders" eventId="event-1" messageId="message-1" resolutionStatus="Failed" />);
    await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(1));
    expect(screen.queryByText("Failure intelligence")).toBeNull();
  });

  it("clears a previous occurrence when navigating", async () => {
    const fetchMock = vi.fn()
      .mockResolvedValueOnce(new Response(JSON.stringify({ status: "Ready", canAnalyze: false, contractVersion: 1 })))
      .mockResolvedValueOnce(new Response(JSON.stringify({ category: "old_failure", categoryConfidence: 1 })))
      .mockResolvedValueOnce(new Response(null, { status: 404 }));
    globalThis.fetch = fetchMock as typeof fetch;
    const view = render(<IntelligenceCard endpointId="orders" eventId="event-1" messageId="message-1" resolutionStatus="Failed" />);
    await screen.findByText("old_failure (100%)");
    view.rerender(<IntelligenceCard endpointId="orders" eventId="event-2" messageId="message-2" resolutionStatus="Failed" />);
    await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(3));
    expect(screen.queryByText("old_failure (100%)")).toBeNull();
  });
  it("shows existing results to Readers without offering paid analysis", async () => {
    globalThis.fetch = vi.fn()
      .mockResolvedValueOnce(new Response(JSON.stringify({ status: "Ready", canAnalyze: false, contractVersion: 1 })))
      .mockResolvedValueOnce(new Response(JSON.stringify({ category: "business_rule", categoryConfidence: 0.9, retryLikelihood: 0, changeRequiredLikelihood: 1, externalDependencyLikelihood: 0, revision: 2, model: "test", createdAtUtc: "2026-09-20T00:00:00Z" }))) as typeof fetch;
    render(<IntelligenceCard endpointId="orders" eventId="event-1" messageId="message-1" resolutionStatus="Failed" />);
    await screen.findByText("business_rule (90%)");
    expect(screen.queryByRole("button")).toBeNull();
  });

  it("forces a new request after an unknown outcome", async () => {
    const fetchMock = vi.fn()
      .mockResolvedValueOnce(new Response(JSON.stringify({ status: "Ready", canAnalyze: true, contractVersion: 1 })))
      .mockResolvedValueOnce(new Response(JSON.stringify({ code: "AnalysisOutcomeUnknown" }), { status: 409 }))
      .mockResolvedValueOnce(new Response(JSON.stringify({ code: "ProviderUnavailable" }), { status: 503 }));
    globalThis.fetch = fetchMock as typeof fetch;
    render(<IntelligenceCard endpointId="orders" eventId="event-1" messageId="message-1" resolutionStatus="Failed" />);
    await userEvent.click(await screen.findByRole("button", { name: "Re-analyze" }));
    expect(JSON.parse(fetchMock.mock.calls[2][1].body)).toEqual({ force: true });
  });
  it("stays hidden when the feature status route is absent", async () => {
    globalThis.fetch = vi.fn().mockResolvedValue(new Response(null, { status: 404 })) as unknown as typeof fetch;

    render(<IntelligenceCard endpointId="orders" eventId="event-1" messageId="message-1" resolutionStatus="Failed" />);

    await waitFor(() => expect(screen.queryByText("Failure intelligence")).toBeNull());
  });

  it("analyzes the exact failure message occurrence", async () => {
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(new Response(JSON.stringify({ status: "Ready", canAnalyze: true, contractVersion: 1 }), { status: 200 }))
      .mockResolvedValueOnce(new Response(null, { status: 404 }))
      .mockResolvedValueOnce(
        new Response(
          JSON.stringify({
            result: {
              category: "transient_dependency",
              categoryConfidence: 0.91,
              retryLikelihood: 0.82,
              changeRequiredLikelihood: 0.12,
              externalDependencyLikelihood: 0.97,
              guidance: "RetryMayHelp",
              revision: 1,
              model: "jev-1.13.0",
              createdAtUtc: "2026-09-20T00:00:00Z",
            },
            cached: false,
          }),
          { status: 200 },
        ),
      );
    globalThis.fetch = fetchMock as unknown as typeof fetch;

    render(<IntelligenceCard endpointId="orders" eventId="event-1" messageId="failure-message-7" resolutionStatus="DeadLettered" />);

    await userEvent.click(await screen.findByRole("button", { name: "Analyze failure" }));
    await screen.findByText("transient_dependency (91%)");

    const postCall = fetchMock.mock.calls[2];
    expect(postCall[0]).toContain("/failures/event-1/failure-message-7/classification");
    expect((postCall[1] as RequestInit).headers).toMatchObject({ "Idempotency-Key": expect.any(String) });
  });
});
