import { describe, expect, it } from "vitest";
import moment from "moment";
import * as api from "api-client";
import {
  axisTicks,
  buildFlowRows,
  formatSpan,
  MAX_FLOW_ROWS,
} from "./flow-gantt.functions";

const at = (iso: string) => moment(iso);
const message = (
  messageType: string,
  iso: string,
  extra: Partial<api.IMessage> = {},
) =>
  new api.Message({
    messageId: `${messageType}-${iso}`,
    messageType: messageType as api.MessageType,
    enqueuedTimeUtc: at(iso),
    from: "Crm",
    to: "Resolver",
    ...extra,
  });

describe("buildFlowRows", () => {
  it("pairs each request with the next response as a span", () => {
    const { rows, spanMs } = buildFlowRows(
      [
        message("errorResponse", "2026-09-28T17:58:03.356Z", {
          errorContent: new api.ErrorContent({ errorText: "503" }),
        }),
        message("eventRequest", "2026-09-28T17:58:02.114Z", {
          from: "Portal",
          to: "Crm",
        }),
      ],
      [],
    );

    expect(rows.map((r) => r.kind)).toEqual(["request", "error"]);
    expect(rows[0].offsetMs).toBe(0);
    expect(rows[0].durationMs).toBe(1242);
    expect(rows[0].route).toBe("Portal → Crm");
    expect(rows[1].offsetMs).toBe(1242);
    expect(rows[1].durationMs).toBeUndefined();
    expect(rows[1].errorText).toBe("503");
    expect(spanMs).toBe(1242);
  });

  it("numbers retries and leaves an unanswered request as a point", () => {
    const { rows } = buildFlowRows(
      [
        message("eventRequest", "2026-09-28T17:58:00.000Z"),
        message("errorResponse", "2026-09-28T17:58:01.000Z"),
        message("retryRequest", "2026-09-28T17:58:31.000Z"),
        message("errorResponse", "2026-09-28T17:58:32.000Z"),
        message("RetryRequest", "2026-09-28T17:59:32.000Z"),
      ],
      [],
    );

    expect(rows.map((r) => r.label)).toEqual([
      "Event Request",
      "Error",
      "Retry 1",
      "Error",
      "Retry 2",
    ]);
    expect(rows[2].durationMs).toBe(1000);
    expect(rows[4].durationMs).toBeUndefined();
  });

  it("interleaves audits by time as points", () => {
    const { rows } = buildFlowRows(
      [
        message("errorResponse", "2026-09-28T17:58:01.000Z"),
        message("resubmissionRequest", "2026-09-28T18:06:30.455Z"),
      ],
      [
        new api.MessageAudit({
          auditorName: "operator",
          auditTimestamp: at("2026-09-28T18:06:30.402Z"),
          auditType: api.MessageAuditAuditType.Resubmit,
        }),
        // Reads are audited too, but are not part of the message's flow.
        new api.MessageAudit({
          auditorName: "operator",
          auditTimestamp: at("2026-09-28T18:07:00.000Z"),
          auditType: api.MessageAuditAuditType.GetEventDetails,
        }),
      ],
    );

    expect(rows.map((r) => r.kind)).toEqual(["error", "audit", "resubmit"]);
    expect(rows[1].label).toBe("Audit · Resubmit");
    expect(rows[1].route).toBe("by operator");
    expect(rows[1].durationMs).toBeUndefined();
  });

  it("keeps the first rows and reports how many it dropped", () => {
    const many = Array.from({ length: MAX_FLOW_ROWS + 5 }, (_, i) =>
      message(
        "errorResponse",
        new Date(Date.UTC(2026, 8, 28, 18, 0, i)).toISOString(),
      ),
    );
    const { rows, hidden } = buildFlowRows(many, []);
    expect(rows).toHaveLength(MAX_FLOW_ROWS);
    expect(hidden).toBe(5);
  });

  it("is empty without history", () => {
    expect(buildFlowRows([], [])).toEqual({ rows: [], spanMs: 0, hidden: 0 });
  });
});

describe("axisTicks", () => {
  it("picks a round step giving at most five intervals", () => {
    expect(axisTicks(510_000)).toEqual([0, 120_000, 240_000, 360_000, 480_000]);
    expect(axisTicks(1_242)).toEqual([0, 500, 1_000]);
  });

  it("has a single tick for a zero-length flow", () => {
    expect(axisTicks(0)).toEqual([0]);
  });
});

describe("formatSpan", () => {
  it("reads like a stopwatch", () => {
    expect(formatSpan(0)).toBe("0");
    expect(formatSpan(41)).toBe("41 ms");
    expect(formatSpan(1_242)).toBe("1.24 s");
    expect(formatSpan(120_000)).toBe("2m");
    expect(formatSpan(125_000)).toBe("2m 05s");
    expect(formatSpan(3_600_000)).toBe("1h");
  });
});
