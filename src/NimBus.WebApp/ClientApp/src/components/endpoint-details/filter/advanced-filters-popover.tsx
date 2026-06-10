import { useEffect, useRef, useState } from "react";
import { Button } from "components/ui/button";
import { cn } from "lib/utils";
import {
  EMPTY_ADVANCED_FILTERS,
  advancedChips,
  advancedCount,
  type AdvancedFilters,
} from "./advanced-filters";

// Sliders glyph for the "Advanced filters" trigger (inlined to match the
// existing inline-SVG icon pattern in event-filtering.tsx).
const SlidersIcon = () => (
  <svg
    className="h-3.5 w-3.5"
    viewBox="0 0 24 24"
    fill="none"
    stroke="currentColor"
    strokeWidth={2}
    strokeLinecap="round"
    strokeLinejoin="round"
  >
    <line x1="4" y1="21" x2="4" y2="14" />
    <line x1="4" y1="10" x2="4" y2="3" />
    <line x1="12" y1="21" x2="12" y2="12" />
    <line x1="12" y1="8" x2="12" y2="3" />
    <line x1="20" y1="21" x2="20" y2="16" />
    <line x1="20" y1="12" x2="20" y2="3" />
    <line x1="1" y1="14" x2="7" y2="14" />
    <line x1="9" y1="8" x2="15" y2="8" />
    <line x1="17" y1="16" x2="23" y2="16" />
  </svg>
);

const RemoveIcon = () => (
  <svg
    className="h-2.5 w-2.5"
    viewBox="0 0 24 24"
    fill="none"
    stroke="currentColor"
    strokeWidth={2.6}
    strokeLinecap="round"
  >
    <path d="M5 5l14 14M19 5L5 19" />
  </svg>
);

interface AdvancedFiltersPopoverProps {
  // Currently-applied advanced filters — drives the count badge / active state
  // and seeds the popover inputs on open.
  value: AdvancedFilters;
  // Commit a new set of advanced filters (popover Apply).
  onApply: (next: AdvancedFilters) => void;
  // Which edge the floating panel aligns to. Use "right" when the trigger sits
  // near the right edge (e.g. next to the column chooser) so the 420px panel
  // opens inward instead of off-screen.
  align?: "left" | "right";
}

const inputClass =
  "w-full rounded-md border border-border-strong bg-background px-2.5 py-2 font-mono text-xs text-foreground focus:border-primary focus:outline-none focus:ring-2 focus:ring-primary-200";

// The "Advanced filters" trigger + its floating popover (Updated/Added ranges +
// Payload). Renders only the control; the active-filter chips are rendered
// separately via <AdvancedFilterChips> so they can live on their own row (zero
// footprint when idle). The popover floats over the page so the results table
// never shifts. Chips/count derive from the applied `value`.
export default function AdvancedFiltersPopover({
  value,
  onApply,
  align = "left",
}: AdvancedFiltersPopoverProps) {
  const [open, setOpen] = useState(false);
  const [draft, setDraft] = useState<AdvancedFilters>(value);
  const wrapRef = useRef<HTMLDivElement>(null);

  const count = advancedCount(value);

  // Seed the draft from the applied value each time the popover opens, so a
  // cancelled edit is discarded and re-opening reflects what's actually applied.
  const openPopover = () => {
    setDraft(value);
    setOpen(true);
  };

  useEffect(() => {
    if (!open) return;
    const onDocMouseDown = (e: MouseEvent) => {
      if (!wrapRef.current?.contains(e.target as Node)) setOpen(false);
    };
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") setOpen(false);
    };
    document.addEventListener("mousedown", onDocMouseDown);
    document.addEventListener("keydown", onKey);
    return () => {
      document.removeEventListener("mousedown", onDocMouseDown);
      document.removeEventListener("keydown", onKey);
    };
  }, [open]);

  const setField = (key: keyof AdvancedFilters, v: string) =>
    setDraft((d) => ({ ...d, [key]: v }));

  const apply = () => {
    onApply(draft);
    setOpen(false);
  };

  return (
    <div ref={wrapRef} className="relative inline-flex">
      <button
        type="button"
        aria-haspopup="dialog"
        aria-expanded={open}
        onClick={() => (open ? setOpen(false) : openPopover())}
        className={cn(
          "inline-flex items-center gap-1.5 rounded-md border px-3 py-1.5 text-xs font-semibold",
          count > 0
            ? "border-primary bg-primary-tint text-primary-600"
            : "border-border-strong bg-card text-muted-foreground hover:bg-surface-2",
        )}
      >
        <SlidersIcon />
        Advanced filters
        {count > 0 && (
          <span className="inline-flex h-4 min-w-[16px] items-center justify-center rounded-full bg-primary px-1 font-mono text-[10px] font-bold text-white">
            {count}
          </span>
        )}
      </button>

      {open && (
        <div
          role="dialog"
          aria-label="Advanced filters"
          className={cn(
            "absolute top-[calc(100%+8px)] z-40 w-[420px] rounded-nb-lg border border-border-strong bg-card p-4 text-left shadow-nb-lg",
            align === "right" ? "right-0" : "left-0",
          )}
        >
          <p className="mb-3 font-mono text-[10px] font-bold uppercase tracking-[0.13em] text-muted-foreground">
            Advanced filters
          </p>

          <div className="mb-3">
            <label className="mb-1.5 block text-xs font-semibold text-foreground">
              Updated
            </label>
            <div className="grid grid-cols-2 gap-2">
              <input
                type="datetime-local"
                aria-label="Updated from"
                className={inputClass}
                value={draft.updatedFrom}
                onChange={(e) => setField("updatedFrom", e.target.value)}
              />
              <input
                type="datetime-local"
                aria-label="Updated to"
                className={inputClass}
                value={draft.updatedTo}
                onChange={(e) => setField("updatedTo", e.target.value)}
              />
            </div>
          </div>

          <div className="mb-3">
            <label className="mb-1.5 block text-xs font-semibold text-foreground">
              Added
            </label>
            <div className="grid grid-cols-2 gap-2">
              <input
                type="datetime-local"
                aria-label="Added from"
                className={inputClass}
                value={draft.addedFrom}
                onChange={(e) => setField("addedFrom", e.target.value)}
              />
              <input
                type="datetime-local"
                aria-label="Added to"
                className={inputClass}
                value={draft.addedTo}
                onChange={(e) => setField("addedTo", e.target.value)}
              />
            </div>
          </div>

          <div className="mb-3">
            <label className="mb-1.5 block text-xs font-semibold text-foreground">
              Payload contains
            </label>
            <input
              type="text"
              aria-label="Payload contains"
              placeholder="e.g. orderId:4471"
              className={inputClass}
              value={draft.payload}
              onChange={(e) => setField("payload", e.target.value)}
            />
          </div>

          <div className="flex items-center gap-2 border-t border-border pt-3">
            <button
              type="button"
              onClick={() => setDraft(EMPTY_ADVANCED_FILTERS)}
              className="mr-auto text-xs font-semibold text-muted-foreground hover:text-status-danger"
            >
              Clear
            </button>
            <Button
              type="button"
              variant="outline"
              colorScheme="gray"
              size="sm"
              onClick={() => setOpen(false)}
            >
              Cancel
            </Button>
            <Button
              type="button"
              variant="solid"
              colorScheme="primary"
              size="sm"
              onClick={apply}
            >
              Apply
            </Button>
          </div>
        </div>
      )}
    </div>
  );
}

interface AdvancedFilterChipsProps {
  value: AdvancedFilters;
  onApply: (next: AdvancedFilters) => void;
}

// Removable chips for the applied advanced filters (one per active field) plus a
// "Clear all" when more than one is set. Renders nothing when no advanced filter
// is active, so it occupies no space until needed.
export function AdvancedFilterChips({
  value,
  onApply,
}: AdvancedFilterChipsProps) {
  const chips = advancedChips(value);
  if (chips.length === 0) return null;

  return (
    <div className="flex flex-wrap items-center gap-2">
      {chips.map((chip) => (
        <span
          key={chip.key}
          className="inline-flex items-center gap-1.5 rounded-full border border-border-strong bg-card py-1 pl-3 pr-1.5 text-xs font-semibold text-muted-foreground"
        >
          <span className="font-mono text-[10px] uppercase tracking-wide text-muted-foreground">
            {chip.label}
          </span>
          <span className="font-mono text-[11px] text-foreground">
            {chip.display}
          </span>
          <button
            type="button"
            aria-label={`Remove ${chip.label}`}
            onClick={() => onApply({ ...value, [chip.key]: "" })}
            className="inline-flex h-[17px] w-[17px] items-center justify-center rounded-full bg-surface-2 text-muted-foreground hover:bg-status-danger-50 hover:text-status-danger"
          >
            <RemoveIcon />
          </button>
        </span>
      ))}
      {chips.length > 1 && (
        <button
          type="button"
          onClick={() => onApply(EMPTY_ADVANCED_FILTERS)}
          className="px-1.5 text-xs font-semibold text-primary-600 hover:underline"
        >
          Clear all
        </button>
      )}
    </div>
  );
}
