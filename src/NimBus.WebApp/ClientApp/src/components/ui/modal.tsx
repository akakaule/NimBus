import { useEffect, useRef, type ReactNode } from "react";
import { createPortal } from "react-dom";
import { cn } from "lib/utils";

export interface ModalProps {
  isOpen: boolean;
  onClose: () => void;
  children: ReactNode;
  size?: "sm" | "md" | "lg" | "xl" | "2xl" | "full";
  closeOnOverlayClick?: boolean;
  closeOnEsc?: boolean;
  /** Accessible name of the dialog. */
  label?: string;
}

const Modal = ({
  isOpen,
  onClose,
  children,
  size = "md",
  closeOnOverlayClick = true,
  closeOnEsc = true,
  label,
}: ModalProps) => {
  const overlayRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (!isOpen) return;
    // Capture phase, and mark the key handled: a Modal opened over a SidePanel
    // must take Escape before the panel's document listener closes the panel.
    const handleEsc = (e: KeyboardEvent) => {
      if (e.key !== "Escape") return;
      e.preventDefault();
      if (closeOnEsc) onClose();
    };
    document.addEventListener("keydown", handleEsc, true);
    // Restore whatever lock was there before (a SidePanel's), not a blank value.
    const overflow = document.body.style.overflow;
    document.body.style.overflow = "hidden";

    return () => {
      document.removeEventListener("keydown", handleEsc, true);
      document.body.style.overflow = overflow;
    };
  }, [isOpen, onClose, closeOnEsc]);

  if (!isOpen) return null;

  const sizes = {
    sm: "max-w-sm",
    md: "max-w-md",
    lg: "max-w-lg",
    xl: "max-w-xl",
    "2xl": "max-w-5xl",
    full: "max-w-[90vw]",
  };

  const handleOverlayClick = (e: React.MouseEvent) => {
    if (closeOnOverlayClick && e.target === overlayRef.current) {
      onClose();
    }
  };

  return createPortal(
    <div
      ref={overlayRef}
      className="fixed inset-0 z-50 flex items-center justify-center bg-black/50 p-4"
      onClick={handleOverlayClick}
    >
      <div
        className={cn(
          "relative w-full bg-card text-card-foreground rounded-lg shadow-xl max-h-[85vh] overflow-y-auto",
          "animate-zoom-in",
          sizes[size],
        )}
        role="dialog"
        aria-modal="true"
        aria-label={label}
      >
        {children}
      </div>
    </div>,
    document.body,
  );
};

const ModalHeader = ({
  children,
  className,
  onClose,
}: {
  children: ReactNode;
  className?: string;
  onClose?: () => void;
}) => (
  <div
    className={cn(
      "flex items-center justify-between p-4 border-b border-border",
      className,
    )}
  >
    <h2 className="text-lg font-semibold text-card-foreground">{children}</h2>
    {onClose && (
      <button
        onClick={onClose}
        className="p-1 text-muted-foreground hover:text-foreground rounded-md hover:bg-accent transition-colors"
        aria-label="Close"
      >
        <svg
          className="w-5 h-5"
          fill="none"
          stroke="currentColor"
          viewBox="0 0 24 24"
        >
          <path
            strokeLinecap="round"
            strokeLinejoin="round"
            strokeWidth={2}
            d="M6 18L18 6M6 6l12 12"
          />
        </svg>
      </button>
    )}
  </div>
);

const ModalBody = ({
  children,
  className,
}: {
  children: ReactNode;
  className?: string;
}) => <div className={cn("p-4", className)}>{children}</div>;

const ModalFooter = ({
  children,
  className,
}: {
  children: ReactNode;
  className?: string;
}) => (
  <div
    className={cn(
      "flex items-center justify-end gap-2 p-4 border-t border-border",
      className,
    )}
  >
    {children}
  </div>
);

export { Modal, ModalHeader, ModalBody, ModalFooter };
