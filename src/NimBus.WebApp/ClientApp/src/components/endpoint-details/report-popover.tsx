import { useEffect, useLayoutEffect, useRef, useState } from "react";
import { createPortal } from "react-dom";
import { normalizeTicketId } from "functions/reported.functions";

interface ReportPopoverProps {
  // The "Report" button the popover anchors under.
  anchor: HTMLElement;
  // Called with the normalized ticket id, or null to mark without a ticket.
  onSubmit: (ticketId: string | null) => void;
  onClose: () => void;
}

// Small ticket-capture popover for the Reported column. Rendered in a portal
// and positioned under the anchor so it is never clipped by the data-table's
// overflow scroller. Closes on outside-click / Esc.
export default function ReportPopover({
  anchor,
  onSubmit,
  onClose,
}: ReportPopoverProps) {
  const popRef = useRef<HTMLDivElement>(null);
  const inputRef = useRef<HTMLInputElement>(null);
  const [value, setValue] = useState("");
  const [invalid, setInvalid] = useState(false);
  const [coords, setCoords] = useState<{ top: number; left: number }>({
    top: -9999,
    left: -9999,
  });

  const WIDTH = 280;

  useLayoutEffect(() => {
    const r = anchor.getBoundingClientRect();
    const left = Math.min(Math.max(8, r.left), window.innerWidth - WIDTH - 8);
    setCoords({ top: r.bottom + 6, left });
  }, [anchor]);

  useEffect(() => {
    const t = setTimeout(() => inputRef.current?.focus(), 20);
    return () => clearTimeout(t);
  }, []);

  useEffect(() => {
    const onDocMouseDown = (e: MouseEvent) => {
      const target = e.target as Node;
      if (
        popRef.current &&
        !popRef.current.contains(target) &&
        !anchor.contains(target)
      ) {
        onClose();
      }
    };
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") onClose();
    };
    document.addEventListener("mousedown", onDocMouseDown);
    document.addEventListener("keydown", onKey);
    return () => {
      document.removeEventListener("mousedown", onDocMouseDown);
      document.removeEventListener("keydown", onKey);
    };
  }, [anchor, onClose]);

  // Empty input → mark without a ticket (null); invalid input → inline error.
  const submit = () => {
    const normalized = normalizeTicketId(value);
    if (normalized === undefined) {
      setInvalid(true);
      return;
    }
    onSubmit(normalized);
  };

  return createPortal(
    <div
      ref={popRef}
      role="dialog"
      aria-label="Mark as reported"
      style={{ position: "fixed", top: coords.top, left: coords.left, width: WIDTH }}
      className="z-50 rounded-md border border-border-strong bg-card p-3.5 text-left shadow-xl"
      onClick={(e) => e.stopPropagation()}
    >
      <h5 className="mb-1 text-sm font-bold text-foreground">
        Mark as reported
      </h5>
      <p className="mb-2.5 text-[11.5px] text-muted-foreground">
        Paste the ticket this event was reported under (optional).
      </p>
      <div className="flex items-center gap-1.5 rounded-md border border-border-strong bg-background px-2.5 focus-within:border-status-info focus-within:ring-2 focus-within:ring-status-info-50">
        <input
          ref={inputRef}
          value={value}
          onChange={(e) => {
            setValue(e.target.value);
            setInvalid(false);
          }}
          onKeyDown={(e) => {
            if (e.key === "Enter") submit();
          }}
          placeholder="e.g. INC0428771 or OPS-42"
          autoComplete="off"
          className="w-full flex-1 border-0 bg-transparent py-2 font-mono text-[12.5px] text-foreground outline-none"
        />
      </div>
      {invalid && (
        <p className="mt-1 text-[11px] text-status-danger">
          Letters, digits, &quot;.&quot;, &quot;_&quot; and &quot;-&quot; only (max 64 chars).
        </p>
      )}
      <button
        type="button"
        onClick={submit}
        className="mt-2.5 w-full rounded-md bg-status-info px-3 py-2 text-xs font-semibold text-white hover:opacity-90"
      >
        Mark reported
      </button>
      <button
        type="button"
        onClick={() => onSubmit(null)}
        className="mt-2 block w-full text-center text-[11px] text-muted-foreground underline hover:text-primary-600"
      >
        Mark without a ticket
      </button>
    </div>,
    document.body,
  );
}
