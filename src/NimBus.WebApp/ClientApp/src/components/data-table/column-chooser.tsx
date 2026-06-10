import { useEffect, useRef, useState } from "react";
import { cn } from "lib/utils";

// Inline SVG glyphs, matching the existing inline-icon pattern used by
// neighbouring components (no icon package dependency).
const ColumnsIcon = () => (
  <svg
    className="h-3.5 w-3.5 text-muted-foreground"
    viewBox="0 0 24 24"
    fill="none"
    stroke="currentColor"
    strokeWidth={2}
    strokeLinecap="round"
    strokeLinejoin="round"
  >
    <rect x="3" y="3" width="18" height="18" rx="2" />
    <line x1="9" y1="3" x2="9" y2="21" />
    <line x1="15" y1="3" x2="15" y2="21" />
  </svg>
);

const CheckIcon = () => (
  <svg
    className="h-[11px] w-[11px]"
    viewBox="0 0 24 24"
    fill="none"
    stroke="currentColor"
    strokeWidth={3}
    strokeLinecap="round"
    strokeLinejoin="round"
  >
    <path d="M20 6L9 17l-5-5" />
  </svg>
);

export interface ColumnOption {
  id: string;
  label: string;
  // Locked columns are always visible and cannot be toggled (e.g. the row's
  // identity column). They render with an "always" badge.
  locked?: boolean;
}

interface ColumnChooserProps {
  columns: ColumnOption[];
  // The ids the user has hidden. Locked ids are ignored even if present.
  hidden: Set<string>;
  onToggle: (id: string) => void;
  onReset: () => void;
}

// "Columns" picker for a data table — lets operators show/hide columns.
// Selection state is owned by the parent (which persists it); this component
// only renders the button + panel and handles open/close with outside-click /
// Esc dismissal. The panel is absolutely positioned under the trigger, which is
// safe here because the view row has no clipping/scroll ancestor.
export default function ColumnChooser({
  columns,
  hidden,
  onToggle,
  onReset,
}: ColumnChooserProps) {
  const [open, setOpen] = useState(false);
  const wrapRef = useRef<HTMLDivElement>(null);

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

  const visibleCount = columns.filter(
    (c) => c.locked || !hidden.has(c.id),
  ).length;
  const hasHidden = columns.some((c) => !c.locked && hidden.has(c.id));

  return (
    <div ref={wrapRef} className="relative inline-flex">
      <button
        type="button"
        aria-haspopup="menu"
        aria-expanded={open}
        aria-label="Choose visible columns"
        onClick={() => setOpen((o) => !o)}
        className={cn(
          "inline-flex items-center gap-2 rounded-md border border-border-strong bg-card px-3 py-1.5",
          "text-xs font-semibold text-muted-foreground hover:bg-accent/50",
          open && "bg-accent/50",
        )}
      >
        <ColumnsIcon />
        Columns
        <span className="font-mono text-[11px] text-muted-foreground">
          {visibleCount}/{columns.length}
        </span>
      </button>

      {open && (
        <div
          role="menu"
          className="absolute right-0 top-[calc(100%+8px)] z-50 w-64 rounded-nb-md border border-border-strong bg-card p-2 shadow-nb-lg"
        >
          <div className="mb-1 flex items-center justify-between border-b border-border px-2 pb-2 pt-1">
            <span className="font-mono text-[10px] font-bold uppercase tracking-[0.13em] text-muted-foreground">
              Visible columns
            </span>
            <button
              type="button"
              onClick={onReset}
              disabled={!hasHidden}
              className="text-[11.5px] font-semibold text-primary-600 hover:underline disabled:cursor-default disabled:no-underline disabled:opacity-40"
            >
              Reset
            </button>
          </div>

          {columns.map((c) => {
            const checked = c.locked || !hidden.has(c.id);

            if (c.locked) {
              return (
                <div
                  key={c.id}
                  className="flex items-center gap-2.5 rounded-md px-2 py-1.5 text-sm text-muted-foreground"
                >
                  <span className="inline-flex h-[17px] w-[17px] items-center justify-center rounded-[5px] border border-border bg-surface-2 text-muted-foreground">
                    <CheckIcon />
                  </span>
                  {c.label}
                  <span className="ml-auto rounded-[3px] bg-surface-2 px-1.5 py-0.5 font-mono text-[8.5px] uppercase tracking-wide text-muted-foreground">
                    always
                  </span>
                </div>
              );
            }

            return (
              <button
                key={c.id}
                type="button"
                role="menuitemcheckbox"
                aria-checked={checked}
                onClick={() => onToggle(c.id)}
                className="flex w-full items-center gap-2.5 rounded-md px-2 py-1.5 text-left text-sm text-foreground hover:bg-accent/50"
              >
                <span
                  className={cn(
                    "inline-flex h-[17px] w-[17px] items-center justify-center rounded-[5px] border",
                    checked
                      ? "border-primary bg-primary text-white"
                      : "border-border-strong bg-card text-transparent",
                  )}
                >
                  <CheckIcon />
                </span>
                {c.label}
              </button>
            );
          })}
        </div>
      )}
    </div>
  );
}
