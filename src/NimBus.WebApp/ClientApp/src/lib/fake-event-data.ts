import { v4 as uuid } from "uuid";
import type * as api from "api-client";

// Tiny in-house faker for the Compose Event modal. Mirrors the demo apps'
// `samples/CrmErpDemo/Crm.Web/src/fakeData.ts` so values feel consistent
// across the demo surface — and avoids pulling @faker-js/faker into the
// admin bundle just for "fill the form with something plausible".

// ---- Pools (kept in sync with the demo apps' fakeData.ts) -----------------

const COMPANIES = [
  "Contoso",
  "Acme",
  "Globex",
  "Initech",
  "Umbrella",
  "Soylent",
  "Stark Industries",
  "Wayne Enterprises",
  "Pied Piper",
  "Hooli",
  "Massive Dynamic",
  "Cyberdyne",
  "Tyrell",
  "Aperture",
  "Black Mesa",
] as const;

const COMPANY_SUFFIXES = [
  "A/S",
  "GmbH",
  "Ltd",
  "Inc",
  "BV",
  "AB",
  "Oy",
  "SA",
  "NV",
] as const;

const COUNTRIES = [
  "DE",
  "DK",
  "SE",
  "NO",
  "FI",
  "GB",
  "FR",
  "NL",
  "ES",
  "IT",
  "US",
] as const;

const FIRST_NAMES = [
  "Anna",
  "Bjarke",
  "Camilla",
  "David",
  "Elisabeth",
  "Frederik",
  "Gitte",
  "Henrik",
  "Ida",
  "Jens",
  "Karen",
  "Lars",
  "Mette",
  "Niels",
  "Ole",
  "Pia",
  "Rasmus",
  "Signe",
  "Thomas",
  "Ulla",
] as const;

const LAST_NAMES = [
  "Hansen",
  "Nielsen",
  "Jensen",
  "Pedersen",
  "Andersen",
  "Sørensen",
  "Larsen",
  "Christensen",
  "Kristensen",
  "Olsen",
  "Rasmussen",
  "Madsen",
  "Thomsen",
  "Schmidt",
  "Mortensen",
] as const;

const EMAIL_DOMAINS = [
  "example.com",
  "example.dk",
  "example.de",
  "example.io",
  "test.local",
] as const;

const PHONE_PREFIXES = [
  "+45",
  "+49",
  "+46",
  "+47",
  "+44",
  "+33",
  "+1",
] as const;

const CITIES = [
  "Copenhagen",
  "Berlin",
  "Stockholm",
  "Oslo",
  "Helsinki",
  "London",
  "Paris",
  "Amsterdam",
  "Madrid",
  "Milan",
  "New York",
] as const;

const STREETS = [
  "Strandvejen",
  "Hauptstraße",
  "Kungsgatan",
  "Karl Johans gate",
  "Mannerheimintie",
  "Baker Street",
  "Rue de Rivoli",
] as const;

const CURRENCIES = ["EUR", "USD", "DKK", "SEK", "NOK", "GBP"] as const;

const WORDS = [
  "alpha",
  "bravo",
  "charlie",
  "delta",
  "echo",
  "foxtrot",
  "golf",
  "hotel",
  "india",
  "juliet",
  "kilo",
] as const;

// ---- Helpers --------------------------------------------------------------

function pick<T>(arr: readonly T[]): T {
  return arr[Math.floor(Math.random() * arr.length)] as T;
}

function shortSuffix(): string {
  return Math.random().toString(36).slice(2, 5).toUpperCase();
}

function randomDigits(n: number): string {
  let out = "";
  for (let i = 0; i < n; i++) out += Math.floor(Math.random() * 10).toString();
  return out;
}

function randomInt(min: number, max: number): number {
  return Math.floor(Math.random() * (max - min + 1)) + min;
}

function todayIsoDate(): string {
  return new Date().toISOString().slice(0, 10);
}

function nowIsoTime(): string {
  return new Date().toISOString().slice(11, 19);
}

// ---- Name-based rules — first match wins ---------------------------------
// Matched against the lowercased property name. Order matters: place more
// specific patterns above the generic ones (e.g. `customernumber` before the
// catch-all `number$` rule).

const NAME_RULES: Array<{ match: RegExp; gen: () => unknown }> = [
  { match: /^email$|email$/, gen: () => randomEmail() },
  { match: /^phone$|phonenumber$/, gen: () => randomPhone() },
  { match: /^firstname$|^givenname$/, gen: () => pick(FIRST_NAMES) },
  { match: /^lastname$|^surname$|^familyname$/, gen: () => pick(LAST_NAMES) },
  { match: /^fullname$|^displayname$/, gen: () => `${pick(FIRST_NAMES)} ${pick(LAST_NAMES)}` },
  {
    match: /^legalname$|^companyname$|^organi[sz]ation(name)?$/,
    gen: () => `${pick(COMPANIES)} ${pick(COMPANY_SUFFIXES)} ${shortSuffix()}`,
  },
  { match: /^countrycode$|^country$/, gen: () => pick(COUNTRIES) },
  { match: /^taxid$|^vatnumber$|^vat$/, gen: () => `TAX-${randomDigits(7)}` },
  { match: /^customernumber$|^accountnumber$|^ordernumber$/, gen: () => randomDigits(6) },
  { match: /^city$/, gen: () => pick(CITIES) },
  { match: /^street(name|address)?$|^address(line\d?)?$/, gen: () => `${pick(STREETS)} ${randomInt(1, 199)}` },
  { match: /^zip(code)?$|^postalcode$/, gen: () => randomDigits(4) },
  { match: /^currency(code)?$/, gen: () => pick(CURRENCIES) },
];

function randomEmail(): string {
  const first = pick(FIRST_NAMES).toLowerCase();
  const last = pick(LAST_NAMES).toLowerCase();
  return `${first}.${last}@${pick(EMAIL_DOMAINS)}`;
}

function randomPhone(): string {
  return `${pick(PHONE_PREFIXES)} ${randomDigits(2)} ${randomDigits(2)} ${randomDigits(2)} ${randomDigits(2)}`;
}

// ---- Type-based fallback --------------------------------------------------

function valueForType(typeName: string | undefined): unknown {
  switch (typeName) {
    case "Guid":
      return uuid();
    case "String":
      return pick(WORDS);
    case "Int16":
    case "Int32":
    case "Int64":
    case "Long":
    case "Integer":
      return randomInt(1, 1000);
    case "Boolean":
    case "Bool":
      return Math.random() < 0.5;
    case "Decimal":
    case "Double":
    case "Single":
    case "Float":
      return Math.round(Math.random() * 100000) / 100;
    case "DateTime":
    case "DateTimeOffset":
      return new Date().toISOString();
    case "DateOnly":
      return todayIsoDate();
    case "TimeOnly":
      return nowIsoTime();
    default:
      // Unknown types (enums like CustomerOrigin, nested POCOs) — keep the
      // existing placeholder behaviour so the field still signals its shape.
      return typeName ?? "value";
  }
}

// ---- Public entry point ---------------------------------------------------

/**
 * Build a JSON payload for the given event-type schema, filled with realistic
 * fake data. Returns a pretty-printed JSON string (2-space indent) so the
 * caller can drop it straight into the Compose Event textarea.
 *
 * Rules:
 *  1. `MessageMetadata` is skipped (NimBus stamps it server-side).
 *  2. Property name rules win first (email, phone, legalName, …).
 *  3. CLR type rules cover Guid / String / numerics / booleans / dates.
 *  4. Anything we don't recognise (enums, nested POCOs) keeps the legacy
 *     `typeName` placeholder so the field still hints at its shape.
 */
export function generateFakeEventPayload(
  properties: api.EventTypeProperty[] | undefined,
): string {
  const obj: Record<string, unknown> = {};
  for (const p of properties ?? []) {
    if (!p.name || p.name === "MessageMetadata") continue;
    obj[p.name] = generateValue(p.name, p.typeName);
  }
  return JSON.stringify(obj, null, 2);
}

function generateValue(name: string, typeName: string | undefined): unknown {
  const lc = name.toLowerCase();
  for (const rule of NAME_RULES) {
    if (rule.match.test(lc)) return rule.gen();
  }
  return valueForType(typeName);
}
