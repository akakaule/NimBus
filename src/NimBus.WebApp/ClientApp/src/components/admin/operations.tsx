import { useState, useEffect } from "react";
import * as api from "api-client";
import { Accordion } from "components/ui/accordion";
import { OperationGroup } from "./operation-group";
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
import { EndpointControlsCard } from "./endpoint-controls";

interface EndpointOption {
  value: string;
  label: string;
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
          id="endpoint-controls"
          tone="warning"
          icon="⏻"
          title="Endpoint Kill Switch"
          count={1}
          caption="Reversible · per-endpoint"
          description="Enable or disable each endpoint's receive (processing) and send (publishing) independently via Service Bus entity status."
        >
          <EndpointControlsCard endpoints={endpoints} />
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
