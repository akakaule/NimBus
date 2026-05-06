import { useState, useEffect } from "react";
import * as api from "api-client";
import {
  Accordion,
  AccordionItem,
  AccordionTrigger,
  AccordionContent,
} from "components/ui/accordion";
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

function SectionBadge({ count, color }: { count: number; color: string }) {
  return (
    <span
      className={`inline-flex items-center justify-center text-xs font-medium rounded-full px-2 py-0.5 ${color}`}
    >
      {count} {count === 1 ? "operation" : "operations"}
    </span>
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

  return (
    <div className="w-full">
      <Accordion allowMultiple={true} defaultExpandedItems={["recovery"]}>
        {/* ── Recovery (green) ── */}
        <AccordionItem id="recovery">
          <AccordionTrigger
            itemId="recovery"
            className="rounded-t-lg border-l-4 border-l-green-500"
          >
            <div className="flex items-center gap-3">
              <svg className="w-5 h-5 text-green-600 dark:text-green-400" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M4 4v5h.582m15.356 2A8.001 8.001 0 004.582 9m0 0H9m11 11v-5h-.581m0 0a8.003 8.003 0 01-15.357-2m15.357 2H15" />
              </svg>
              <span className="text-base font-semibold">Recovery</span>
              <SectionBadge count={3} color="bg-green-100 text-green-800 dark:bg-green-900/40 dark:text-green-200" />
            </div>
          </AccordionTrigger>
          <AccordionContent itemId="recovery">
            <p className="text-sm text-muted-foreground mb-4">
              Recover from failures by resubmitting, skipping, or reprocessing messages.
            </p>
            <div className="grid grid-cols-1 lg:grid-cols-2 gap-4">
              <BulkResubmitCard endpoints={endpoints} />
              <SkipMessagesCard endpoints={endpoints} />
              <SessionPurgeCard endpoints={endpoints} />
            </div>
          </AccordionContent>
        </AccordionItem>

        {/* ── Cleanup (amber) ── */}
        <AccordionItem id="cleanup">
          <AccordionTrigger
            itemId="cleanup"
            className="border-l-4 border-l-amber-500"
          >
            <div className="flex items-center gap-3">
              <svg className="w-5 h-5 text-amber-600 dark:text-amber-400" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M19 7l-.867 12.142A2 2 0 0116.138 21H7.862a2 2 0 01-1.995-1.858L5 7m5 4v6m4-6v6m1-10V4a1 1 0 00-1-1h-4a1 1 0 00-1 1v3M4 7h16" />
              </svg>
              <span className="text-base font-semibold">Cleanup</span>
              <SectionBadge count={4} color="bg-amber-100 text-amber-800 dark:bg-amber-900/40 dark:text-amber-200" />
            </div>
          </AccordionTrigger>
          <AccordionContent itemId="cleanup">
            <p className="text-sm text-muted-foreground mb-4">
              Remove resolved, dead-lettered, or specific messages.
            </p>
            <div className="grid grid-cols-1 lg:grid-cols-2 gap-4">
              <DeleteDeadLetteredCard endpoints={endpoints} />
              <DeleteByStatusCard endpoints={endpoints} />
              <DeleteMessagesByToCard />
              <DeleteEventCard endpoints={endpoints} />
            </div>
          </AccordionContent>
        </AccordionItem>

        {/* ── Infrastructure (blue) ── */}
        <AccordionItem id="infrastructure">
          <AccordionTrigger
            itemId="infrastructure"
            className="border-l-4 border-l-blue-500"
          >
            <div className="flex items-center gap-3">
              <svg className="w-5 h-5 text-blue-600 dark:text-blue-400" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M10.325 4.317c.426-1.756 2.924-1.756 3.35 0a1.724 1.724 0 002.573 1.066c1.543-.94 3.31.826 2.37 2.37a1.724 1.724 0 001.066 2.573c1.756.426 1.756 2.924 0 3.35a1.724 1.724 0 00-1.066 2.573c.94 1.543-.826 3.31-2.37 2.37a1.724 1.724 0 00-2.573 1.066c-.426 1.756-2.924 1.756-3.35 0a1.724 1.724 0 00-2.573-1.066c-1.543.94-3.31-.826-2.37-2.37a1.724 1.724 0 00-1.066-2.573c-1.756-.426-1.756-2.924 0-3.35a1.724 1.724 0 001.066-2.573c-.94-1.543.826-3.31 2.37-2.37.996.608 2.296.07 2.572-1.065z" />
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M15 12a3 3 0 11-6 0 3 3 0 016 0z" />
              </svg>
              <span className="text-base font-semibold">Infrastructure</span>
              <SectionBadge count={2} color="bg-blue-100 text-blue-800 dark:bg-blue-900/40 dark:text-blue-200" />
            </div>
          </AccordionTrigger>
          <AccordionContent itemId="infrastructure">
            <p className="text-sm text-muted-foreground mb-4">
              Service Bus subscription management and cross-environment data operations.
            </p>
            <div className="grid grid-cols-1 lg:grid-cols-2 gap-4">
              <SubscriptionPurgeCard endpoints={endpoints} />
              <CopyEndpointCard endpoints={endpoints} />
            </div>
          </AccordionContent>
        </AccordionItem>

        {/* ── Danger Zone (red, collapsed by default) ── */}
        <AccordionItem id="danger">
          <AccordionTrigger
            itemId="danger"
            className="rounded-b-lg border-l-4 border-l-red-500"
          >
            <div className="flex items-center gap-3">
              <svg className="w-5 h-5 text-red-600 dark:text-red-400" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 9v2m0 4h.01m-6.938 4h13.856c1.54 0 2.502-1.667 1.732-2.5L13.732 4.5c-.77-.833-2.694-.833-3.464 0L3.34 16.5c-.77.833.192 2.5 1.732 2.5z" />
              </svg>
              <span className="text-base font-semibold text-red-700 dark:text-red-300">Danger Zone</span>
              <SectionBadge count={1} color="bg-red-100 text-red-800 dark:bg-red-900/40 dark:text-red-200" />
            </div>
          </AccordionTrigger>
          <AccordionContent itemId="danger">
            <div className="border border-red-200 bg-red-50 dark:border-red-900/60 dark:bg-red-950/30 rounded-lg p-4">
              <p className="text-sm text-red-700 dark:text-red-300 mb-4">
                These operations are irreversible and will permanently delete data. Use with extreme caution.
              </p>
              <div className="grid grid-cols-1 lg:grid-cols-2 gap-4">
                <DeleteAllEventsCard endpoints={endpoints} />
              </div>
            </div>
          </AccordionContent>
        </AccordionItem>
      </Accordion>
    </div>
  );
}
