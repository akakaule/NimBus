import { useEffect, useState } from "react";
import { Input } from "components/ui/input";
import { Button } from "components/ui/button";
import { Select } from "components/ui/select";
import {
  Client,
  CookieAuth,
  AuditSearchFilter,
  AuditSearchFilterAuditType,
} from "api-client";

interface AuditFilterBarProps {
  onSearch: (filter: AuditSearchFilter) => void;
  isLoading: boolean;
}

const auditTypeOptions = Object.entries(AuditSearchFilterAuditType).map(
  ([key, value]) => ({ label: key, value }),
);

export default function AuditFilterBar({
  onSearch,
  isLoading,
}: AuditFilterBarProps) {
  const [endpointId, setEndpointId] = useState("");
  const [eventId, setEventId] = useState("");
  const [auditorName, setAuditorName] = useState("");
  const [eventTypeId, setEventTypeId] = useState("");
  const [auditType, setAuditType] = useState<string>("");
  const [createdFrom, setCreatedFrom] = useState("");
  const [createdTo, setCreatedTo] = useState("");
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
    const filter = new AuditSearchFilter();
    if (endpointId) filter.endpointId = endpointId;
    if (eventId) filter.eventId = eventId;
    if (auditorName) filter.auditorName = auditorName;
    if (eventTypeId) filter.eventTypeId = eventTypeId;
    if (auditType)
      filter.auditType = auditType as AuditSearchFilterAuditType;
    if (createdFrom) filter.createdAtFrom = new Date(createdFrom) as any;
    if (createdTo) filter.createdAtTo = new Date(createdTo) as any;
    onSearch(filter);
  };

  const handleReset = () => {
    setEndpointId("");
    setEventId("");
    setAuditorName("");
    setEventTypeId("");
    setAuditType("");
    setCreatedFrom("");
    setCreatedTo("");
    onSearch(new AuditSearchFilter());
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
            Auditor
          </label>
          <Input
            value={auditorName}
            onChange={(e) => setAuditorName(e.target.value)}
            onKeyDown={handleKeyDown}
            placeholder="Filter by auditor..."
          />
        </div>
        <div>
          <label className="block text-xs font-medium text-muted-foreground mb-1">
            Event Type
          </label>
          <Input
            value={eventTypeId}
            onChange={(e) => setEventTypeId(e.target.value)}
            onKeyDown={handleKeyDown}
            placeholder="Filter by event type..."
          />
        </div>
        <div>
          <label className="block text-xs font-medium text-muted-foreground mb-1">
            Action Type
          </label>
          <select
            value={auditType}
            onChange={(e) => setAuditType(e.target.value)}
            className="flex h-10 w-full rounded-md border border-input bg-background px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-offset-0 focus:border-primary focus:ring-primary-200"
          >
            <option value="">All types</option>
            {auditTypeOptions.map((opt) => (
              <option key={opt.value} value={opt.value}>
                {opt.label}
              </option>
            ))}
          </select>
        </div>
        <div>
          <label className="block text-xs font-medium text-muted-foreground mb-1">
            Created From
          </label>
          <Input
            type="datetime-local"
            value={createdFrom}
            onChange={(e) => setCreatedFrom(e.target.value)}
            onKeyDown={handleKeyDown}
          />
        </div>
        <div>
          <label className="block text-xs font-medium text-muted-foreground mb-1">
            Created To
          </label>
          <Input
            type="datetime-local"
            value={createdTo}
            onChange={(e) => setCreatedTo(e.target.value)}
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
