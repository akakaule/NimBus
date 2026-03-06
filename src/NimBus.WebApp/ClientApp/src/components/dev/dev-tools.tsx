import { useEffect, useState } from "react";
import * as api from "api-client";
import { Button } from "components/ui/button";
import {
  Card,
  CardContent,
  CardHeader,
  CardTitle,
  CardDescription,
} from "components/ui/card";
import { Input } from "components/ui/input";
import { Label } from "components/ui/label";
import { Select } from "components/ui/select";
import { Spinner } from "components/ui/spinner";

interface SeedResult {
  message: string;
  endpointsCreated: number;
  eventsCreated: number;
  messagesCreated: number;
  auditsCreated: number;
}

function generatePayload(eventType?: api.EventType): string {
  const now = new Date().toISOString();
  const getStringSample = (propertyName: string): string => {
    const key = propertyName.toLowerCase();
    if (key.includes("email")) return "integration@acme.example";
    if (key.includes("phone")) return "+45 70112233";
    if (key.includes("fax")) return "+45 70998877";
    if (key.includes("vat")) return "DK12345678";
    if (key.includes("country")) return "DK";
    if (key.includes("city")) return "Copenhagen";
    if (key.includes("postcode") || key.includes("postal")) return "2100";
    if (key.includes("address2")) return "Suite 4";
    if (key.includes("address")) return "Nyhavn 1";
    if (key.includes("county")) return "Hovedstaden";
    if (
      key.includes("homepage") ||
      key.includes("website") ||
      key.includes("url")
    )
      return "https://acme.example";
    if (key.includes("language")) return "EN";
    if (key.includes("department")) return "SALES";
    if (key.includes("salesperson")) return "SP001";
    if (key.includes("customernumber") || key.includes("customerno"))
      return "C-10042";
    if (key.includes("prospectno")) return "P-9001";
    if (key.includes("companyreg")) return "CVR-44332211";
    if (key.includes("duns")) return "123456789";
    if (key.includes("businessunit")) return "Wholesale";
    if (key.includes("name2")) return "Nordic Division";
    if (key.includes("searchname")) return "ACME";
    if (key.includes("name")) return "Acme Corporation";
    if (key.includes("lastmodifiedby")) return "dev-tools";
    return `Sample ${propertyName}`;
  };

  const getNumberSample = (propertyName: string): number => {
    const key = propertyName.toLowerCase();
    if (key.includes("id")) return 1001;
    if (key.includes("potential")) return 750000;
    if (key.includes("blocking")) return 0;
    if (key.includes("adjusted")) return 1;
    if (key.includes("revenue")) return 1200000;
    return 42;
  };

  const getSampleValue = (
    typeName: string | undefined,
    propertyName: string,
  ): unknown => {
    const normalized = (typeName ?? "").toLowerCase();
    if (normalized.includes("guid")) return crypto.randomUUID();
    if (normalized.includes("datetime") || normalized.includes("date"))
      return new Date().toISOString();
    if (normalized.includes("bool")) return false;
    if (
      normalized.includes("int") ||
      normalized.includes("long") ||
      normalized.includes("short") ||
      normalized.includes("decimal") ||
      normalized.includes("double") ||
      normalized.includes("float")
    ) {
      return getNumberSample(propertyName);
    }
    return getStringSample(propertyName);
  };

  const payloadFromProperties: Record<string, unknown> = {};
  eventType?.properties?.forEach((prop) => {
    if (prop.name && prop.name !== "MessageMetadata") {
      payloadFromProperties[prop.name] = getSampleValue(
        prop.typeName,
        prop.name,
      );
    }
  });
  if (Object.keys(payloadFromProperties).length > 0) {
    return JSON.stringify(payloadFromProperties, null, 2);
  }

  const eventTypeId = eventType?.id ?? "";
  const templates: Record<string, object> = {
    CustomerChanged: {
      customerNo: "C-10042",
      name: "Acme Corp",
      action: "Updated",
      timestamp: now,
    },
    OrderCreated: {
      orderNo: "SO-20456",
      customerNo: "C-10042",
      totalAmount: 1250.0,
      currency: "DKK",
      timestamp: now,
    },
    ItemChanged: {
      itemNo: "ITEM-3001",
      description: "Widget Pro",
      unitPrice: 49.95,
      action: "Updated",
      timestamp: now,
    },
    InvoicePosted: {
      invoiceNo: "INV-80123",
      customerNo: "C-10042",
      amount: 2500.0,
      currency: "DKK",
      timestamp: now,
    },
  };

  const payload = templates[eventTypeId] ?? {
    id: crypto.randomUUID(),
    timestamp: now,
    data: {},
  };

  return JSON.stringify(payload, null, 2);
}

export default function DevTools() {
  const [seeding, setSeeding] = useState(false);
  const [clearing, setClearing] = useState(false);
  const [sendingMessage, setSendingMessage] = useState(false);
  const [seedResult, setSeedResult] = useState<SeedResult | null>(null);
  const [statusMessage, setStatusMessage] = useState("");

  // Shared dropdown data
  const [endpoints, setEndpoints] = useState<string[]>([]);

  // Send message form state
  const [msgEndpointId, setMsgEndpointId] = useState("");
  const [msgEventTypeId, setMsgEventTypeId] = useState("");
  const [msgEventTypes, setMsgEventTypes] = useState<api.EventType[]>([]);
  const [msgSessionId, setMsgSessionId] = useState("");
  const [msgContent, setMsgContent] = useState("");

  useEffect(() => {
    const client = new api.Client(api.CookieAuth());
    client
      .getEndpointsAll()
      .then((res) => {
        setEndpoints(res);
        if (res.length > 0) {
          setMsgEndpointId(res[0]);
        }
      })
      .catch(() => {});
  }, []);

  // Fetch endpoint-scoped event types for Send Message when endpoint changes
  useEffect(() => {
    if (!msgEndpointId) return;
    const client = new api.Client(api.CookieAuth());
    client
      .getEventtypesByEndpointId(msgEndpointId)
      .then((res) => {
        const eventTypesById = new Map<string, api.EventType>();
        (res.produces ?? []).forEach((group) => {
          (group.events ?? []).forEach((eventType) => {
            if (eventType.id) {
              eventTypesById.set(eventType.id, eventType);
            }
          });
        });
        (res.eventTypeDetails ?? []).forEach((details) => {
          const detailedEventType = details.eventType;
          if (!detailedEventType?.id) return;

          const existing = eventTypesById.get(detailedEventType.id);
          if (
            existing?.properties?.length &&
            !detailedEventType.properties?.length
          ) {
            eventTypesById.set(detailedEventType.id, existing);
            return;
          }
          eventTypesById.set(detailedEventType.id, detailedEventType);
        });

        const available = Array.from(eventTypesById.values());
        setMsgEventTypes(available);
        setMsgEventTypeId(available.length > 0 ? available[0].id : "");
      })
      .catch(() => {
        setMsgEventTypes([]);
        setMsgEventTypeId("");
      });
  }, [msgEndpointId]);

  const endpointOptions = endpoints.map((ep) => ({
    value: ep,
    label: ep,
  }));
  const selectedMsgEventType = msgEventTypes.find(
    (et) => et.id === msgEventTypeId,
  );

  async function handleSeed() {
    setSeeding(true);
    setStatusMessage("");
    setSeedResult(null);
    try {
      const res = await fetch("/api/dev/seed", { method: "POST" });
      if (res.ok) {
        const data = await res.json();
        setSeedResult(data);
        setStatusMessage("Sample data seeded successfully");
      } else if (res.status === 404) {
        setStatusMessage(
          "Dev endpoints are only available in Development mode",
        );
      } else {
        setStatusMessage(`Error: ${res.statusText}`);
      }
    } catch (err) {
      setStatusMessage(`Error: ${err}`);
    } finally {
      setSeeding(false);
    }
  }

  async function handleClear() {
    setClearing(true);
    setStatusMessage("");
    setSeedResult(null);
    try {
      const res = await fetch("/api/dev/seed", { method: "DELETE" });
      if (res.ok) {
        setStatusMessage("Sample data cleared");
      } else {
        setStatusMessage(`Error: ${res.statusText}`);
      }
    } catch (err) {
      setStatusMessage(`Error: ${err}`);
    } finally {
      setClearing(false);
    }
  }

  async function handleSendMessage() {
    setSendingMessage(true);
    setStatusMessage("");
    try {
      const res = await fetch("/api/dev/messages", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          endpointId: msgEndpointId,
          eventTypeId: msgEventTypeId,
          sessionId: msgSessionId || undefined,
          messageContent: msgContent || undefined,
        }),
      });
      if (res.ok) {
        setStatusMessage(`Message sent to ${msgEndpointId}`);
      } else {
        setStatusMessage(`Error: ${res.statusText}`);
      }
    } catch (err) {
      setStatusMessage(`Error: ${err}`);
    } finally {
      setSendingMessage(false);
    }
  }

  return (
    <div className="space-y-6">
      {statusMessage && (
        <div className="p-3 rounded border bg-muted text-sm">
          {statusMessage}
        </div>
      )}

      <Card>
        <CardHeader>
          <CardTitle>Seed Sample Data</CardTitle>
          <CardDescription>
            Populate the emulator with realistic sample data across all
            containers (endpoints, events, subscriptions).
          </CardDescription>
        </CardHeader>
        <CardContent>
          <div className="flex gap-3">
            <Button onClick={handleSeed} disabled={seeding || clearing}>
              {seeding ? <Spinner /> : "Seed Sample Data"}
            </Button>
            <Button
              onClick={handleClear}
              disabled={seeding || clearing}
              variant="outline"
            >
              {clearing ? <Spinner /> : "Clear Sample Data"}
            </Button>
          </div>

          {seedResult && (
            <div className="mt-4 p-3 rounded border text-sm space-y-1">
              <p>Endpoints: {seedResult.endpointsCreated}</p>
              <p>Events: {seedResult.eventsCreated}</p>
              <p>Messages: {seedResult.messagesCreated}</p>
              <p>Audits: {seedResult.auditsCreated}</p>
            </div>
          )}
        </CardContent>
      </Card>

      <Card>
        <CardHeader>
          <CardTitle>Send Message</CardTitle>
          <CardDescription>
            Send a message to Azure Service Bus as a publisher event request.
          </CardDescription>
        </CardHeader>
        <CardContent>
          <div className="grid grid-cols-2 gap-4 mb-4">
            <div>
              <Label>Endpoint</Label>
              <Select
                value={msgEndpointId}
                onChange={(e) => setMsgEndpointId(e.target.value)}
                options={endpointOptions}
                placeholder="Select endpoint"
              />
            </div>
            <div>
              <Label>Event Type</Label>
              <Select
                value={msgEventTypeId}
                onChange={(e) => setMsgEventTypeId(e.target.value)}
                options={msgEventTypes.map((et) => ({
                  value: et.id,
                  label: et.id,
                }))}
                placeholder="Select event type"
              />
            </div>
            <div>
              <Label>Session ID (optional)</Label>
              <Input
                value={msgSessionId}
                onChange={(e) => setMsgSessionId(e.target.value)}
                placeholder="Auto-generated if empty"
              />
            </div>
          </div>
          <div className="mb-4">
            <div className="flex items-center justify-between mb-1">
              <Label>Message Payload (optional JSON)</Label>
              <Button
                variant="outline"
                size="sm"
                type="button"
                onClick={() =>
                  setMsgContent(generatePayload(selectedMsgEventType))
                }
              >
                Generate Payload
              </Button>
            </div>
            <textarea
              className="w-full h-24 rounded border px-3 py-2 font-mono text-sm"
              value={msgContent}
              onChange={(e) => setMsgContent(e.target.value)}
              placeholder='{"customerNo": "10001", "name": "Acme Corp"}'
            />
          </div>
          <Button
            onClick={handleSendMessage}
            disabled={sendingMessage || !msgEndpointId}
          >
            {sendingMessage ? <Spinner /> : "Send Message"}
          </Button>
        </CardContent>
      </Card>
    </div>
  );
}
