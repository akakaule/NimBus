import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import * as api from "api-client";
import ResolverDeadLetterDialog from "./resolver-dead-letter-dialog";

afterEach(() => cleanup());

describe("ResolverDeadLetterDialog", () => {
  it("keeps missing and empty reasons distinct and submits an exact reason", async () => {
    const getOverview = vi.fn().mockResolvedValue({
      totalMessageCount: 3,
      isTruncated: false,
      snapshotLimit: 500,
      reasons: [
        { reason: null, count: 1 },
        { reason: "", count: 1 },
        { reason: "CosmosDbThrottled", count: 1 },
      ],
    });
    const resubmit = vi.fn().mockResolvedValue({
      processed: 1,
      succeeded: 1,
      failed: 0,
      errors: [],
    });
    const client = {
      getAdminServicebusResolverDeadletters: getOverview,
      postAdminServicebusResolverDeadlettersResubmit: resubmit,
    } as unknown as api.Client;

    render(
      <ResolverDeadLetterDialog
        client={client}
        subscriptionName="Resolver"
        onClose={() => {}}
        onReplayed={() => Promise.resolve()}
      />,
    );

    expect(await screen.findByText("No reason")).toBeDefined();
    expect(screen.getByText("Empty reason")).toBeDefined();
    fireEvent.click(screen.getByLabelText(/CosmosDbThrottled/));
    fireEvent.click(screen.getByRole("button", { name: "Resubmit 1 message" }));

    await waitFor(() => expect(resubmit).toHaveBeenCalledWith(
      { scope: api.DeadLetterResubmitRequestScope.Reason, reason: "CosmosDbThrottled" },
      "Resolver",
    ));
    expect(await screen.findByText("Resubmitted 1 of 1 message(s).")).toBeDefined();
  });

  it("explains a truncated snapshot and submits all without a reason", async () => {
    const resubmit = vi.fn().mockResolvedValue({
      processed: 500,
      succeeded: 500,
      failed: 0,
      errors: [],
    });
    const client = {
      getAdminServicebusResolverDeadletters: vi.fn().mockResolvedValue({
        totalMessageCount: 500,
        isTruncated: true,
        snapshotLimit: 500,
        reasons: [{ reason: "CosmosDbThrottled", count: 500 }],
      }),
      postAdminServicebusResolverDeadlettersResubmit: resubmit,
    } as unknown as api.Client;

    render(
      <ResolverDeadLetterDialog
        client={client}
        subscriptionName="Resolver"
        onClose={() => {}}
        onReplayed={() => Promise.resolve()}
      />,
    );

    expect(await screen.findByText(/first 500-message snapshot batch/)).toBeDefined();
    expect(screen.getByLabelText(/All messages in this snapshot/)).toBeDefined();
    fireEvent.click(screen.getByRole("button", { name: "Resubmit 500 messages" }));

    await waitFor(() => expect(resubmit).toHaveBeenCalledWith(
      { scope: api.DeadLetterResubmitRequestScope.All },
      "Resolver",
    ));
  });
});
