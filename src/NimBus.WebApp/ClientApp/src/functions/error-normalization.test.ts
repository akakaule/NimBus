import { describe, expect, it } from "vitest";
import { normalizeErrorPattern } from "./error-normalization";

describe("normalizeErrorPattern", () => {
  it("groups errors that differ only by a trailing unquoted value", () => {
    const prefix =
      "[CRM API ERROR] Failed to update Contact 'a4b1c2d3-1111-2222-3333-444455556666': A contact with the same email address already exists. Please use a different email or update the existing contact: ";
    const first = normalizeErrorPattern(`${prefix}james.burton`);
    const second = normalizeErrorPattern(`${prefix}Isabel Gsaller`);
    const third = normalizeErrorPattern(`${prefix}support`);

    expect(second).toBe(first);
    expect(third).toBe(first);
    expect(first).toBe(
      "[CRM API ERROR] Failed to update Contact '<id>': A contact with the same email address already exists. Please use a different email or update the existing contact: <value>",
    );
  });

  it("keeps the reason after a single category colon", () => {
    expect(normalizeErrorPattern("Order rejected: timeout")).toBe(
      "Order rejected: timeout",
    );
    expect(normalizeErrorPattern("Order rejected: invalid credentials")).toBe(
      "Order rejected: invalid credentials",
    );
  });

  it("matches the server normalizer for quoted ids, numbers and Action suffix", () => {
    expect(
      normalizeErrorPattern(
        "[TRANSIENT ERROR] Failed to handle AliceSaidHello 'hello-0': the downstream system was momentarily unavailable. Action: Wait a few minutes and resubmit",
      ),
    ).toBe(
      "[TRANSIENT ERROR] Failed to handle AliceSaidHello '<value>': the downstream system was momentarily unavailable",
    );
    expect(
      normalizeErrorPattern(
        "Mandatory fields must be filled in to proceed. The dimension value 55890030 does not exist for dimension Afdeling.",
      ),
    ).toBe(
      "Mandatory fields must be filled in to proceed. The dimension value <value> does not exist for dimension Afdeling",
    );
    expect(
      normalizeErrorPattern(
        "Job with JobID [06E53DA9-0FA8-495B-8DE3-9187FF5F83BF] not found",
      ),
    ).toBe("Job with JobID [<id>] not found");
  });

  it("returns Unknown for empty input", () => {
    expect(normalizeErrorPattern(undefined)).toBe("Unknown");
    expect(normalizeErrorPattern("")).toBe("Unknown");
  });
});
