import { useState, useEffect } from "react";
import * as api from "api-client";
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
} from "./advanced-operations";
import { SessionPurgeCard } from "./session-management";

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

  return (
    <div className="space-y-8 w-full">
      <section>
        <h3 className="text-lg font-semibold mb-4 text-muted-foreground">
          Messages
        </h3>
        <div className="grid grid-cols-1 lg:grid-cols-2 gap-4">
          <BulkResubmitCard endpoints={endpoints} />
          <SkipMessagesCard endpoints={endpoints} />
          <DeleteByStatusCard endpoints={endpoints} />
          <DeleteDeadLetteredCard endpoints={endpoints} />
          <DeleteMessagesByToCard />
          <DeleteEventCard endpoints={endpoints} />
        </div>
      </section>

      <section>
        <h3 className="text-lg font-semibold mb-4 text-muted-foreground">
          Subscriptions
        </h3>
        <div className="grid grid-cols-1 lg:grid-cols-2 gap-4">
          <SubscriptionPurgeCard endpoints={endpoints} />
        </div>
      </section>

      <section>
        <h3 className="text-lg font-semibold mb-4 text-muted-foreground">
          Sessions
        </h3>
        <div className="grid grid-cols-1 lg:grid-cols-2 gap-4">
          <SessionPurgeCard endpoints={endpoints} />
        </div>
      </section>

      <section>
        <h3 className="text-lg font-semibold mb-4 text-muted-foreground">
          Data
        </h3>
        <div className="grid grid-cols-1 lg:grid-cols-2 gap-4">
          <CopyEndpointCard endpoints={endpoints} />
        </div>
      </section>
    </div>
  );
}
