import { describe, expect, it } from "vitest";
import {
  STALE_COMMAND_MESSAGE,
  describeCommandError,
  isStaleCommand,
} from "./operator-command.functions";

describe("operator command errors", () => {
  it("explains a 409 as a message that changed since it was loaded", () => {
    const caught = { status: 409, message: "Conflict" };
    expect(isStaleCommand(caught)).toBe(true);
    expect(describeCommandError(caught, "resubmit")).toBe(STALE_COMMAND_MESSAGE);
  });

  it("explains a 503 as an unavailable audit log", () => {
    expect(describeCommandError({ status: 503 }, "skip")).toBe(
      "The audit log is unavailable, so the skip was not run. Try again later.",
    );
  });

  it("falls back to a generic message for anything else", () => {
    expect(isStaleCommand(new Error("network"))).toBe(false);
    expect(describeCommandError(new Error("network"), "skip")).toBe(
      "The skip failed.",
    );
    expect(describeCommandError(null, "resubmit")).toBe(
      "The resubmit failed.",
    );
  });
});
