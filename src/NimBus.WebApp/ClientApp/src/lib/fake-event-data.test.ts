import { describe, it, expect } from "vitest";
import type * as api from "api-client";
import { generateFakeEventPayload } from "./fake-event-data";

// Minimal factory so tests don't have to ceremonially `new` the nswag class.
function prop(name: string, typeName?: string): api.EventTypeProperty {
  return { name, typeName } as api.EventTypeProperty;
}

function parsed(json: string): Record<string, unknown> {
  return JSON.parse(json) as Record<string, unknown>;
}

const UUID_V4 = /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i;

describe("generateFakeEventPayload", () => {
  it("returns an empty object when no properties are supplied", () => {
    expect(generateFakeEventPayload(undefined)).toBe("{}");
    expect(generateFakeEventPayload([])).toBe("{}");
  });

  it("emits a 2-space pretty-printed JSON document", () => {
    const json = generateFakeEventPayload([prop("Foo", "String")]);
    expect(json.startsWith("{\n  \"Foo\":")).toBe(true);
  });

  it("skips MessageMetadata", () => {
    const json = generateFakeEventPayload([
      prop("MessageMetadata", "MessageMetadata"),
      prop("AccountId", "Guid"),
    ]);
    const obj = parsed(json);
    expect(obj).not.toHaveProperty("MessageMetadata");
    expect(obj).toHaveProperty("AccountId");
  });

  it("skips properties with no name", () => {
    const json = generateFakeEventPayload([
      prop("", "String"),
      prop("Id", "Guid"),
    ]);
    const obj = parsed(json);
    expect(Object.keys(obj)).toEqual(["Id"]);
  });

  it("generates a UUID v4 for Guid-typed properties", () => {
    const obj = parsed(generateFakeEventPayload([prop("AccountId", "Guid")]));
    expect(obj.AccountId).toMatch(UUID_V4);
  });

  it("uses the email heuristic for email-named properties", () => {
    const obj = parsed(generateFakeEventPayload([prop("Email", "String")]));
    expect(obj.Email).toMatch(/.+@.+\..+/);
  });

  it("uses the country-code heuristic for CountryCode", () => {
    const obj = parsed(
      generateFakeEventPayload([prop("CountryCode", "String")]),
    );
    expect(obj.CountryCode).toMatch(/^[A-Z]{2}$/);
  });

  it("uses the tax-id heuristic for TaxId", () => {
    const obj = parsed(generateFakeEventPayload([prop("TaxId", "String")]));
    expect(obj.TaxId).toMatch(/^TAX-\d{7}$/);
  });

  it("generates an integer for Int32-typed properties", () => {
    const obj = parsed(generateFakeEventPayload([prop("Quantity", "Int32")]));
    expect(typeof obj.Quantity).toBe("number");
    expect(Number.isInteger(obj.Quantity as number)).toBe(true);
  });

  it("generates an ISO timestamp for DateTime", () => {
    const obj = parsed(generateFakeEventPayload([prop("CreatedAt", "DateTime")]));
    expect(typeof obj.CreatedAt).toBe("string");
    expect(Number.isNaN(Date.parse(obj.CreatedAt as string))).toBe(false);
  });

  it("falls back to the typeName placeholder for unknown types (enums)", () => {
    const obj = parsed(
      generateFakeEventPayload([prop("Origin", "CustomerOrigin")]),
    );
    expect(obj.Origin).toBe("CustomerOrigin");
  });

  it("produces a digit string for CustomerNumber even when typed as String", () => {
    const obj = parsed(
      generateFakeEventPayload([prop("CustomerNumber", "String")]),
    );
    expect(obj.CustomerNumber).toMatch(/^\d{6}$/);
  });

  it("produces a 'Company Suffix XYZ' shape for LegalName", () => {
    const obj = parsed(
      generateFakeEventPayload([prop("LegalName", "String")]),
    );
    // "{Company} {Suffix} {3 uppercase alnum}"
    expect(obj.LegalName).toMatch(/^.+\s.+\s[A-Z0-9]{3}$/);
  });
});
