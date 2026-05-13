import { type ReactNode, Children, isValidElement } from "react";
import { cn } from "lib/utils";

export interface PropertyListProps {
  /** PropertySection elements. */
  children: ReactNode;
  className?: string;
}

export interface PropertySectionProps {
  /** Caps-mono section title (e.g. "Identifiers"). */
  title?: ReactNode;
  /** PropertyRow elements. */
  children: ReactNode;
  className?: string;
}

export interface PropertyRowProps {
  /** Field label (left column). */
  label: ReactNode;
  /** Value (right column). */
  value: ReactNode;
  /** When true, renders the value in mono + tabular-nums (e.g. for IDs/timestamps). */
  mono?: boolean;
  className?: string;
}

/**
 * Surface card containing one or more PropertySection blocks (design system §08
 * "Property list / KV list"). Use for the canonical detail-page sidebar of an
 * Event, EventType, Endpoint, etc. — pairs label and value into a hairline
 * grid that survives long values without reflowing the page.
 */
export const PropertyList: React.FC<PropertyListProps> = ({
  children,
  className,
}) => {
  // Cast children once so we can apply a "first-section has no top border" rule.
  const sections = Children.toArray(children).filter(isValidElement);
  return (
    <div
      className={cn(
        "bg-card border border-border rounded-nb-md overflow-hidden",
        className,
      )}
    >
      {sections}
    </div>
  );
};

export const PropertySection: React.FC<PropertySectionProps> = ({
  title,
  children,
  className,
}) => (
  <section className={className}>
    {title && (
      <div
        className={cn(
          "font-mono text-[10.5px] uppercase tracking-[0.12em]",
          "text-muted-foreground bg-muted px-5 py-2 border-t border-border",
          "first:border-t-0",
        )}
      >
        {title}
      </div>
    )}
    <dl className="m-0">{children}</dl>
  </section>
);

export const PropertyRow: React.FC<PropertyRowProps> = ({
  label,
  value,
  mono = false,
  className,
}) => (
  <div
    className={cn(
      "grid grid-cols-[minmax(140px,200px)_1fr] gap-4 px-5 py-2.5",
      "border-t border-border text-[13px]",
      className,
    )}
  >
    <dt className="text-muted-foreground font-semibold">{label}</dt>
    <dd
      className={cn(
        "m-0 text-foreground break-words",
        mono && "font-mono text-[12px] tabular-nums",
      )}
    >
      {value}
    </dd>
  </div>
);
