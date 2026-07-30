import { type ReactNode } from "react";
import {
  AccordionItem,
  AccordionTrigger,
  AccordionContent,
} from "components/ui/accordion";
import { cn } from "lib/utils";

// Blast-radius tone — design rec §09 admin grouping rec: group by what the
// operation does to state, not by name. Tone drives the rail colour, the
// icon badge background, and the count-chip palette.
export type Tone = "success" | "warning" | "info" | "danger";

const toneStyles: Record<
  Tone,
  {
    rail: string;
    ico: string;
    countChip: string;
    captionColor: string;
  }
> = {
  success: {
    rail: "border-l-status-success",
    ico: "bg-status-success text-white",
    countChip:
      "bg-status-success-50 text-status-success-ink dark:bg-green-950/40 dark:text-green-200",
    captionColor: "text-status-success",
  },
  warning: {
    rail: "border-l-status-warning",
    ico: "bg-status-warning text-white",
    countChip:
      "bg-status-warning-50 text-status-warning-ink dark:bg-yellow-950/40 dark:text-yellow-200",
    captionColor: "text-status-warning-ink dark:text-yellow-300",
  },
  info: {
    rail: "border-l-status-info",
    ico: "bg-status-info text-white",
    countChip:
      "bg-status-info-50 text-status-info-ink dark:bg-blue-950/40 dark:text-blue-200",
    captionColor: "text-status-info",
  },
  danger: {
    rail: "border-l-status-danger",
    ico: "bg-status-danger text-white",
    countChip:
      "bg-status-danger-50 text-status-danger-ink dark:bg-red-950/40 dark:text-red-200",
    captionColor: "text-status-danger",
  },
};

export interface OperationGroupProps {
  id: string;
  tone: Tone;
  icon: ReactNode;
  title: string;
  count: number;
  /** Right-aligned blast-radius caption (e.g. "Safe · reversible"). */
  caption?: string;
  description: string;
  children: ReactNode;
}

/**
 * Visual wrapper around AccordionItem that picks up the design's
 * `.accordion` + `.acc-rail` + `.acc-ico` + `.acc-count` pattern.
 *
 * The coloured rail on the left + status-tinted icon badge + blast-radius
 * caption give operators a glance-level read on what each group can do.
 * Shared by the Admin page's Operations and Topology tabs so both group
 * their features identically.
 */
export function OperationGroup({
  id,
  tone,
  icon,
  title,
  count,
  caption,
  description,
  children,
}: OperationGroupProps) {
  const styles = toneStyles[tone];
  return (
    <AccordionItem id={id} className="mb-3">
      {/* Rail is a border-left (not an inner div behind overflow-hidden) so
          absolutely-positioned children like the Combobox dropdown can
          escape the card without being clipped. */}
      <div
        className={cn(
          "flex bg-card border border-border border-l-4 rounded-nb-md",
          styles.rail,
        )}
      >
        <div className="flex-1 min-w-0">
          <AccordionTrigger
            itemId={id}
            className="border-0 hover:bg-transparent data-[expanded]:bg-transparent py-4 px-5 rounded-t-nb-md"
          >
            <div className="flex items-center gap-2.5 flex-1 min-w-0">
              <span
                className={cn(
                  "w-6 h-6 inline-flex items-center justify-center rounded-full text-[13px] font-bold shrink-0",
                  styles.ico,
                )}
                aria-hidden="true"
              >
                {icon}
              </span>
              <span className="text-base font-bold">{title}</span>
              <span
                className={cn(
                  "inline-flex items-center justify-center font-mono text-[11px] font-semibold",
                  "px-2 py-0.5 rounded-full",
                  styles.countChip,
                )}
              >
                {count} {count === 1 ? "operation" : "operations"}
              </span>
              {caption && (
                <span
                  className={cn(
                    "ml-auto font-mono text-[11px] hidden sm:inline pr-2",
                    styles.captionColor,
                  )}
                >
                  {caption}
                </span>
              )}
            </div>
          </AccordionTrigger>
          <AccordionContent itemId={id} className="px-5 pb-5 pt-0">
            <p className="text-[13px] text-muted-foreground mb-4 mt-0">
              {description}
            </p>
            {children}
          </AccordionContent>
        </div>
      </div>
    </AccordionItem>
  );
}
