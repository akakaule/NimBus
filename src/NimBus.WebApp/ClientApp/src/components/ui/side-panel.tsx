import { useEffect, useRef, type ReactNode } from "react";
import { createPortal } from "react-dom";
import { cn } from "lib/utils";

export interface SidePanelProps {
  isOpen: boolean;
  onClose: () => void;
  /** Accessible name of the dialog. */
  label: string;
  children: ReactNode;
  className?: string;
}

const FOCUSABLE =
  'a[href], button:not([disabled]), input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])';

/**
 * A dialog anchored to the right edge over a dimmed page, like Application Insights'
 * transaction details. Escape and a click on the backdrop close it; focus stays inside while
 * it is open and returns to what had it when it closes.
 */
export const SidePanel = ({
  isOpen,
  onClose,
  label,
  children,
  className,
}: SidePanelProps) => {
  const panelRef = useRef<HTMLDivElement>(null);
  const onCloseRef = useRef(onClose);
  onCloseRef.current = onClose;

  useEffect(() => {
    if (!isOpen) return;
    const returnTo = document.activeElement as HTMLElement | null;
    const panel = panelRef.current;
    // Focus the dialog itself: screen readers announce it, and Tab moves into its controls.
    panel?.focus();

    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") {
        // Leave Escape to an open popover or menu inside the panel first.
        if (e.defaultPrevented) return;
        onCloseRef.current();
        return;
      }
      if (e.key !== "Tab" || !panel) return;
      const items = Array.from(panel.querySelectorAll<HTMLElement>(FOCUSABLE));
      if (items.length === 0) return;
      const first = items[0];
      const last = items[items.length - 1];
      if (e.shiftKey && document.activeElement === first) {
        e.preventDefault();
        last.focus();
      } else if (!e.shiftKey && document.activeElement === last) {
        e.preventDefault();
        first.focus();
      }
    };
    document.addEventListener("keydown", onKey);
    const overflow = document.body.style.overflow;
    document.body.style.overflow = "hidden";
    return () => {
      document.removeEventListener("keydown", onKey);
      document.body.style.overflow = overflow;
      returnTo?.focus?.();
    };
  }, [isOpen]);

  if (!isOpen) return null;

  return createPortal(
    <div className="fixed inset-0 z-50">
      <div
        aria-hidden="true"
        className="absolute inset-0 bg-black/40 animate-fade-in"
        onClick={() => onCloseRef.current()}
      />
      <div
        ref={panelRef}
        role="dialog"
        aria-modal="true"
        aria-label={label}
        tabIndex={-1}
        className={cn(
          "absolute inset-y-0 right-0 flex w-full max-w-[960px] flex-col border-l border-border-strong bg-card text-card-foreground shadow-nb-lg outline-none animate-slide-in-from-right",
          className,
        )}
      >
        {children}
      </div>
    </div>,
    document.body,
  );
};

export default SidePanel;
