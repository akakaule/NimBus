import { useState, useEffect, type ReactNode } from "react";
import * as api from "api-client";
import {
  Accordion,
  AccordionItem,
  AccordionTrigger,
  AccordionContent,
} from "components/ui/accordion";
import { cn } from "lib/utils";
import {
  BulkResubmitCard,
  DeleteDeadLetteredCard,
  DeleteEventCard,
} from "./bulk-operations";
import {
  SubscriptionPurgeCard,
  DeleteByStatusCard,
  SkipMessagesCard,
  DeleteMessagesByToCard,
  CopyEndpointCard,
  DeleteAllEventsCard,
} from "./advanced-operations";
import { SessionPurgeCard } from "./session-management";

interface EndpointOption {
  value: string;
  label: string;
}

// Blast-radius tone — design rec §09 admin grouping rec: group by what the
// operation does to state, not by name. Tone drives the rail colour, the
// icon badge background, and the count-chip palette.
type Tone = "success" | "warning" | "info" | "danger";

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
    rail: "bg-status-success",
    ico: "bg-status-success text-white",
    countChip:
      "bg-status-success-50 text-status-success-ink dark:bg-green-950/40 dark:text-green-200",
    captionColor: "text-status-success",
  },
  warning: {
    rail: "bg-status-warning",
    ico: "bg-status-warning text-white",
    countChip:
      "bg-status-warning-50 text-status-warning-ink dark:bg-yellow-950/40 dark:text-yellow-200",
    captionColor: "text-status-warning-ink dark:text-yellow-300",
  },
  info: {
    rail: "bg-status-info",
    ico: "bg-status-info text-white",
    countChip:
      "bg-status-info-50 text-status-info-ink dark:bg-blue-950/40 dark:text-blue-200",
    captionColor: "text-status-info",
  },
  danger: {
    rail: "bg-status-danger",
    ico: "bg-status-danger text-white",
    countChip:
      "bg-status-danger-50 text-status-danger-ink dark:bg-red-950/40 dark:text-red-200",
    captionColor: "text-status-danger",
  },
};

interface OperationGroupProps {
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
 */
function OperationGroup({
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
      <div className="flex bg-card border border-border rounded-nb-md overflow-hidden">
        <div className={cn("w-1 shrink-0", styles.rail)} aria-hidden="true" />
        <div className="flex-1 min-w-0">
          <AccordionTrigger
            itemId={id}
            className="border-0 hover:bg-transparent data-[expanded]:bg-transparent py-4 px-5"
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

export default function Operations() {
  const [endpoints, setEndpoints] = useState<EndpointOption[]>([]);

  useEffect(() => {
    loadEndpoints();
  }, []);

  async function loadEndpoints() {
    try {
      const client = new api.Client(api.CookieAuth());
      const config = await client.getAdminPlatformConfig();
      const eps = (config.endpoints ?? []).map((ep) => ({
        value: ep.id ?? "",
        label: ep.name ?? ep.id ?? "",
      }));
      setEndpoints(eps);
    } catch {
      // fallback
    }
  }

  // Design recommendation §09: group operations by *blast radius*, not by
  // name. Operators at 2 a.m. care about "is this safe?" — make that the
  // primary axis. Rails graduate success → warning → info → danger.
  return (
    <div className="w-full">
      <Accordion allowMultiple={true} defaultExpandedItems={["recovery"]}>
        <OperationGroup
          id="recovery"
          tone="success"
          icon="↻"
          title="Recovery"
          count={3}
          caption="Safe · reversible"
          description="Recover from failures by resubmitting, skipping, or reprocessing messages. Idempotent handlers absorb safely."
        >
          <div className="grid grid-cols-1 lg:grid-cols-2 gap-4">
            <BulkResubmitCard endpoints={endpoints} />
            <SkipMessagesCard endpoints={endpoints} />
            <SessionPurgeCard endpoints={endpoints} />
          </div>
        </OperationGroup>

        <OperationGroup
          id="cleanup"
          tone="warning"
          icon="→"
          title="Cleanup"
          count={4}
          caption="Changes state · not re-played"
          description="Remove resolved, dead-lettered, or specific messages without re-processing."
        >
          <div className="grid grid-cols-1 lg:grid-cols-2 gap-4">
            <DeleteDeadLetteredCard endpoints={endpoints} />
            <DeleteByStatusCard endpoints={endpoints} />
            <DeleteMessagesByToCard />
            <DeleteEventCard endpoints={endpoints} />
          </div>
        </OperationGroup>

        <OperationGroup
          id="infrastructure"
          tone="info"
          icon="◇"
          title="Infrastructure"
          count={2}
          caption="Topology · subscriptions"
          description="Service Bus subscription management and cross-environment data operations."
        >
          <div className="grid grid-cols-1 lg:grid-cols-2 gap-4">
            <SubscriptionPurgeCard endpoints={endpoints} />
            <CopyEndpointCard endpoints={endpoints} />
          </div>
        </OperationGroup>

        <OperationGroup
          id="danger"
          tone="danger"
          icon="⚠"
          title="Danger Zone"
          count={1}
          caption="Irreversible · audit-logged"
          description="These operations are irreversible and will permanently delete data. Each requires typing the endpoint name to confirm."
        >
          <div className="border border-status-danger-50 bg-status-danger-50/40 dark:border-red-900/60 dark:bg-red-950/20 rounded-nb-md p-4">
            <div className="grid grid-cols-1 lg:grid-cols-2 gap-4">
              <DeleteAllEventsCard endpoints={endpoints} />
            </div>
          </div>
        </OperationGroup>
      </Accordion>
    </div>
  );
}
