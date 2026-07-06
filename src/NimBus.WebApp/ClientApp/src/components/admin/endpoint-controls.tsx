import { useState, useEffect, useCallback } from "react";
import * as api from "api-client";
import { Button } from "components/ui/button";
import { Badge } from "components/ui/badge";
import {
  Card,
  CardContent,
  CardHeader,
  CardTitle,
  CardDescription,
} from "components/ui/card";
import {
  Modal,
  ModalHeader,
  ModalBody,
  ModalFooter,
} from "components/ui/modal";

interface EndpointOption {
  value: string;
  label: string;
}

// "active" | "disabled" | "not-found" | "unknown" (probe failed) | "loading"
type Status = string;

interface RowState {
  receive: Status;
  send: Status;
}

type Channel = "receive" | "send";
type Action = "enable" | "disable";

interface PendingConfirm {
  endpointId: string;
  channel: Channel;
}

function StatusBadge({ status }: { status: Status }) {
  switch (status) {
    case "active":
      return <Badge variant="success">Enabled</Badge>;
    case "disabled":
      return <Badge variant="error">Disabled</Badge>;
    case "not-found":
      return <Badge variant="warning">Missing</Badge>;
    case "loading":
      return <Badge variant="secondary">…</Badge>;
    default:
      return <Badge variant="secondary">Unknown</Badge>;
  }
}

/**
 * Per-endpoint kill switch. Two independent Service Bus entity-status switches:
 *  - Receive: the endpoint's subscription (Active ↔ ReceiveDisabled) — stops processing.
 *  - Send: the endpoint's topic (Active ↔ SendDisabled) — stops publishing.
 * Because an endpoint's topic is also where its consumed events are auto-forwarded in,
 * disabling send quarantines the topic (inbound forwards dead-letter at the source);
 * the disable-send confirm spells this out.
 */
export function EndpointControlsCard({
  endpoints,
}: {
  endpoints: EndpointOption[];
}) {
  const [rows, setRows] = useState<Record<string, RowState>>({});
  const [busy, setBusy] = useState<Record<string, boolean>>({});
  const [confirm, setConfirm] = useState<PendingConfirm | null>(null);

  const refreshEndpoint = useCallback(async (endpointId: string) => {
    const client = new api.Client(api.CookieAuth());
    try {
      const [receive, send] = await Promise.all([
        client.getEndpointSubscriptionstatus(endpointId).catch(() => "unknown"),
        client.getEndpointSendstatus(endpointId).catch(() => "unknown"),
      ]);
      setRows((prev) => ({ ...prev, [endpointId]: { receive, send } }));
    } catch {
      setRows((prev) => ({
        ...prev,
        [endpointId]: { receive: "unknown", send: "unknown" },
      }));
    }
  }, []);

  useEffect(() => {
    for (const ep of endpoints) {
      setRows((prev) =>
        prev[ep.value]
          ? prev
          : { ...prev, [ep.value]: { receive: "loading", send: "loading" } },
      );
      void refreshEndpoint(ep.value);
    }
  }, [endpoints, refreshEndpoint]);

  async function apply(endpointId: string, channel: Channel, action: Action) {
    setBusy((prev) => ({ ...prev, [endpointId]: true }));
    const client = new api.Client(api.CookieAuth());
    try {
      if (channel === "receive") {
        await client.postEndpointSubscriptionstatus(endpointId, action);
      } else {
        await client.postEndpointSendstatus(endpointId, action);
      }
      await refreshEndpoint(endpointId);
    } catch {
      // Leave the row as-is; a re-probe on next render will reconcile.
    } finally {
      setBusy((prev) => ({ ...prev, [endpointId]: false }));
    }
  }

  // Enabling is safe → apply immediately. Disabling is impactful → confirm first.
  function onToggle(endpointId: string, channel: Channel, current: Status) {
    if (current === "active") {
      setConfirm({ endpointId, channel });
    } else if (current === "disabled") {
      void apply(endpointId, channel, "enable");
    }
  }

  function confirmDisable() {
    if (!confirm) return;
    const { endpointId, channel } = confirm;
    setConfirm(null);
    void apply(endpointId, channel, "disable");
  }

  function ToggleButton({
    endpointId,
    channel,
    status,
  }: {
    endpointId: string;
    channel: Channel;
    status: Status;
  }) {
    const isBusy = busy[endpointId] ?? false;
    if (status === "active") {
      return (
        <Button
          size="xs"
          variant="outline"
          colorScheme="red"
          isLoading={isBusy}
          onClick={() => onToggle(endpointId, channel, status)}
        >
          Disable
        </Button>
      );
    }
    if (status === "disabled") {
      return (
        <Button
          size="xs"
          variant="outline"
          colorScheme="green"
          isLoading={isBusy}
          onClick={() => onToggle(endpointId, channel, status)}
        >
          Enable
        </Button>
      );
    }
    return (
      <Button size="xs" variant="outline" colorScheme="gray" disabled>
        —
      </Button>
    );
  }

  const confirmIsSend = confirm?.channel === "send";

  return (
    <Card>
      <CardHeader>
        <CardTitle>Endpoint Kill Switch</CardTitle>
        <CardDescription>
          Enable or disable each endpoint's <strong>receive</strong> (processing)
          and <strong>send</strong> (publishing) independently. Disabling send
          quarantines the endpoint's topic — while off, events forwarded in from
          other endpoints dead-letter at the source.
        </CardDescription>
      </CardHeader>
      <CardContent>
        <div className="overflow-x-auto">
          <table className="w-full text-sm">
            <thead>
              <tr className="text-left text-xs font-medium text-muted-foreground border-b border-border">
                <th className="py-2 pr-4">Endpoint</th>
                <th className="py-2 pr-4">Receive</th>
                <th className="py-2 pr-4">Send</th>
              </tr>
            </thead>
            <tbody>
              {endpoints.map((ep) => {
                const row = rows[ep.value] ?? {
                  receive: "loading",
                  send: "loading",
                };
                return (
                  <tr key={ep.value} className="border-b border-border/50">
                    <td className="py-2 pr-4 font-medium">{ep.label}</td>
                    <td className="py-2 pr-4">
                      <div className="flex items-center gap-2">
                        <StatusBadge status={row.receive} />
                        <ToggleButton
                          endpointId={ep.value}
                          channel="receive"
                          status={row.receive}
                        />
                      </div>
                    </td>
                    <td className="py-2 pr-4">
                      <div className="flex items-center gap-2">
                        <StatusBadge status={row.send} />
                        <ToggleButton
                          endpointId={ep.value}
                          channel="send"
                          status={row.send}
                        />
                      </div>
                    </td>
                  </tr>
                );
              })}
              {endpoints.length === 0 && (
                <tr>
                  <td
                    colSpan={3}
                    className="py-4 text-center text-muted-foreground"
                  >
                    No endpoints.
                  </td>
                </tr>
              )}
            </tbody>
          </table>
        </div>

        <Modal isOpen={confirm !== null} onClose={() => setConfirm(null)}>
          <ModalHeader onClose={() => setConfirm(null)}>
            Disable {confirmIsSend ? "send" : "receive"} for {confirm?.endpointId}?
          </ModalHeader>
          <ModalBody>
            {confirmIsSend ? (
              <p className="text-sm text-muted-foreground m-0">
                This sets the <strong>{confirm?.endpointId}</strong> topic to
                <span className="font-mono"> SendDisabled</span>: the endpoint can
                no longer publish. Note the topic is also this endpoint's inbox —
                while send is disabled, events auto-forwarded in from other
                endpoints will dead-letter at the source. Re-enabling restores
                normal flow.
              </p>
            ) : (
              <p className="text-sm text-muted-foreground m-0">
                This sets the <strong>{confirm?.endpointId}</strong> subscription to
                <span className="font-mono"> ReceiveDisabled</span>: the endpoint
                stops processing messages. Messages accumulate on the subscription
                until it is re-enabled.
              </p>
            )}
          </ModalBody>
          <ModalFooter>
            <Button
              variant="ghost"
              colorScheme="gray"
              onClick={() => setConfirm(null)}
            >
              Cancel
            </Button>
            <Button variant="solid" colorScheme="red" onClick={confirmDisable}>
              Disable {confirmIsSend ? "send" : "receive"}
            </Button>
          </ModalFooter>
        </Modal>
      </CardContent>
    </Card>
  );
}
