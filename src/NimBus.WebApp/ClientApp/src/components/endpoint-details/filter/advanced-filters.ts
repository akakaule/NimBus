// The "advanced" (less-common) event filters surfaced through the Advanced
// Filters popover: the Updated / Added date ranges and a payload substring.
// These mirror the advanced fields of EndpointFilterParams in events-panel.tsx;
// values are stored as the raw control strings (datetime-local "YYYY-MM-DDTHH:mm"
// or free text), matching the URL params.
export interface AdvancedFilters {
  updatedFrom: string;
  updatedTo: string;
  addedFrom: string;
  addedTo: string;
  payload: string;
}

export const EMPTY_ADVANCED_FILTERS: AdvancedFilters = {
  updatedFrom: "",
  updatedTo: "",
  addedFrom: "",
  addedTo: "",
  payload: "",
};

// Field order + chip labels. Drives both the popover's grouping and the chip
// row (each non-empty field becomes one removable chip, in this order).
export const ADVANCED_FIELDS: ReadonlyArray<{
  key: keyof AdvancedFilters;
  label: string;
}> = [
  { key: "updatedFrom", label: "Updated from" },
  { key: "updatedTo", label: "Updated to" },
  { key: "addedFrom", label: "Added from" },
  { key: "addedTo", label: "Added to" },
  { key: "payload", label: "Payload" },
];

const DATE_KEYS: ReadonlySet<keyof AdvancedFilters> = new Set([
  "updatedFrom",
  "updatedTo",
  "addedFrom",
  "addedTo",
]);

// Human-readable chip value: a datetime-local string "2026-06-01T10:00" reads as
// "2026-06-01 10:00"; payload is shown verbatim (trimmed).
function displayValue(key: keyof AdvancedFilters, raw: string): string {
  const trimmed = (raw ?? "").trim();
  if (!trimmed) return "";
  return DATE_KEYS.has(key) ? trimmed.replace("T", " ") : trimmed;
}

export interface AdvancedChip {
  key: keyof AdvancedFilters;
  label: string;
  display: string;
}

// Active advanced filters as ordered chips; empty fields are omitted.
export function advancedChips(value: AdvancedFilters): AdvancedChip[] {
  return ADVANCED_FIELDS.flatMap(({ key, label }) => {
    const display = displayValue(key, value[key]);
    return display ? [{ key, label, display }] : [];
  });
}

// Number of active advanced filters — the count badge on the trigger.
export function advancedCount(value: AdvancedFilters): number {
  return advancedChips(value).length;
}
