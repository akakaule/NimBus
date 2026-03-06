import { useEffect, useState } from "react";
import { Input } from "components/ui/input";
import { Button } from "components/ui/button";
import { Select } from "components/ui/select";
import {
  Client,
  CookieAuth,
  MessageSearchFilter,
  MessageSearchFilterMessageType,
} from "api-client";

interface MessageFilterBarProps {
  onSearch: (filter: MessageSearchFilter) => void;
  isLoading: boolean;
}

const messageTypeOptions = Object.entries(MessageSearchFilterMessageType).map(
  ([key, value]) => ({ label: key, value }),
);

export default function MessageFilterBar({
  onSearch,
  isLoading,
}: MessageFilterBarProps) {
  const [endpointId, setEndpointId] = useState("");
  const [eventId, setEventId] = useState("");
  const [messageId, setMessageId] = useState("");
  const [sessionId, setSessionId] = useState("");
  const [from, setFrom] = useState("");
  const [to, setTo] = useState("");
  const [messageType, setMessageType] = useState<string>(
    MessageSearchFilterMessageType.EventRequest,
  );
  const [enqueuedFrom, setEnqueuedFrom] = useState("");
  const [enqueuedTo, setEnqueuedTo] = useState("");
  const [endpoints, setEndpoints] = useState<string[]>([]);

  useEffect(() => {
    const client = new Client(CookieAuth());
    client
      .getEndpointsAll()
      .then(setEndpoints)
      .catch(() => setEndpoints([]));
  }, []);

  const endpointOptions = [
    { value: "", label: "All endpoints" },
    ...endpoints.map((endpoint) => ({ value: endpoint, label: endpoint })),
  ];

  const handleSearch = () => {
    const filter = new MessageSearchFilter();
    if (endpointId) filter.endpointId = endpointId;
    if (eventId) filter.eventId = eventId;
    if (messageId) filter.messageId = messageId;
    if (sessionId) filter.sessionId = sessionId;
    if (from) filter.from = from;
    if (to) filter.to = to;
    if (messageType)
      filter.messageType = messageType as MessageSearchFilterMessageType;
    if (enqueuedFrom) filter.enqueuedAtFrom = new Date(enqueuedFrom) as any;
    if (enqueuedTo) filter.enqueuedAtTo = new Date(enqueuedTo) as any;
    onSearch(filter);
  };

  const handleReset = () => {
    setEndpointId("");
    setEventId("");
    setMessageId("");
    setSessionId("");
    setFrom("");
    setTo("");
    setMessageType(MessageSearchFilterMessageType.EventRequest);
    setEnqueuedFrom("");
    setEnqueuedTo("");
    const filter = new MessageSearchFilter();
    filter.messageType = MessageSearchFilterMessageType.EventRequest;
    onSearch(filter);
  };

  const handleKeyDown = (e: React.KeyboardEvent) => {
    if (e.key === "Enter") handleSearch();
  };

  return (
    <div className="bg-muted border border-border rounded-lg p-4 mb-4">
      <div className="grid grid-cols-2 md:grid-cols-3 lg:grid-cols-4 gap-3">
        <div>
          <label className="block text-xs font-medium text-muted-foreground mb-1">
            Endpoint
          </label>
          <Select
            value={endpointId}
            onChange={(e) => setEndpointId(e.target.value)}
            options={endpointOptions}
          />
        </div>
        <div>
          <label className="block text-xs font-medium text-muted-foreground mb-1">
            Event ID
          </label>
          <Input
            value={eventId}
            onChange={(e) => setEventId(e.target.value)}
            onKeyDown={handleKeyDown}
            placeholder="Filter by event ID..."
          />
        </div>
        <div>
          <label className="block text-xs font-medium text-muted-foreground mb-1">
            Message ID
          </label>
          <Input
            value={messageId}
            onChange={(e) => setMessageId(e.target.value)}
            onKeyDown={handleKeyDown}
            placeholder="Filter by message ID..."
          />
        </div>
        <div>
          <label className="block text-xs font-medium text-muted-foreground mb-1">
            Session ID
          </label>
          <Input
            value={sessionId}
            onChange={(e) => setSessionId(e.target.value)}
            onKeyDown={handleKeyDown}
            placeholder="Filter by session ID..."
          />
        </div>
        <div>
          <label className="block text-xs font-medium text-muted-foreground mb-1">
            From (Publisher)
          </label>
          <Select
            value={from}
            onChange={(e) => setFrom(e.target.value)}
            options={endpointOptions}
          />
        </div>
        <div>
          <label className="block text-xs font-medium text-muted-foreground mb-1">
            To (Subscriber)
          </label>
          <Select
            value={to}
            onChange={(e) => setTo(e.target.value)}
            options={endpointOptions}
          />
        </div>
        <div>
          <label className="block text-xs font-medium text-muted-foreground mb-1">
            Message Type
          </label>
          <select
            value={messageType}
            onChange={(e) => setMessageType(e.target.value)}
            className="flex h-10 w-full rounded-md border border-input bg-background px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-offset-0 focus:border-primary focus:ring-primary-200"
          >
            <option value="">All types</option>
            {messageTypeOptions.map((opt) => (
              <option key={opt.value} value={opt.value}>
                {opt.label}
              </option>
            ))}
          </select>
        </div>
        <div>
          <label className="block text-xs font-medium text-muted-foreground mb-1">
            Enqueued From
          </label>
          <Input
            type="datetime-local"
            value={enqueuedFrom}
            onChange={(e) => setEnqueuedFrom(e.target.value)}
            onKeyDown={handleKeyDown}
          />
        </div>
        <div>
          <label className="block text-xs font-medium text-muted-foreground mb-1">
            Enqueued To
          </label>
          <Input
            type="datetime-local"
            value={enqueuedTo}
            onChange={(e) => setEnqueuedTo(e.target.value)}
            onKeyDown={handleKeyDown}
          />
        </div>
      </div>
      <div className="flex gap-2 mt-3">
        <Button
          onClick={handleSearch}
          disabled={isLoading}
          size="sm"
          colorScheme="primary"
        >
          Search
        </Button>
        <Button
          onClick={handleReset}
          disabled={isLoading}
          size="sm"
          variant="outline"
          colorScheme="gray"
        >
          Reset
        </Button>
      </div>
    </div>
  );
}
