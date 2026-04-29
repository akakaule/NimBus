import { useEffect, useMemo, useState } from "react";
import { Input } from "components/ui/input";
import { Button } from "components/ui/button";
import { Select } from "components/ui/select";
import {
  Client,
  CookieAuth,
  AuditSearchFilter,
  AuditSearchFilterAuditType,
} from "api-client";

/**
 * Plain string-keyed shape of the audit filter, suitable for URL-param round-tripping.
 * Declared as a closed `type` so it satisfies the index-signature constraint of `useUrlFilters<T>`.
 */
export type AuditFilterValues = {
  endpointId: string;
  eventId: string;
  auditorName: string;
  eventTypeId: string;
  auditType: string;
  createdFrom: string;
  createdTo: string;
};

export const EMPTY_AUDIT_FILTER: AuditFilterValues = {
  endpointId: "",
  eventId: "",
  auditorName: "",
  eventTypeId: "",
  auditType: "",
  createdFrom: "",
  createdTo: "",
};

export function toAuditSearchFilter(v: AuditFilterValues): AuditSearchFilter {
  const filter = new AuditSearchFilter();
  if (v.endpointId) filter.endpointId = v.endpointId;
  if (v.eventId) filter.eventId = v.eventId;
  if (v.auditorName) filter.auditorName = v.auditorName;
  if (v.eventTypeId) filter.eventTypeId = v.eventTypeId;
  if (v.auditType)
    filter.auditType = v.auditType as AuditSearchFilterAuditType;
  if (v.createdFrom) filter.createdAtFrom = new Date(v.createdFrom) as any;
  if (v.createdTo) filter.createdAtTo = new Date(v.createdTo) as any;
  return filter;
}

interface AuditFilterBarProps {
  value: AuditFilterValues;
  onSearch: (filter: AuditFilterValues) => void;
  onReset: () => void;
  isLoading: boolean;
}

const auditTypeOptions = Object.entries(AuditSearchFilterAuditType).map(
  ([key, value]) => ({ label: key, value }),
);

export default function AuditFilterBar({
  value,
  onSearch,
  onReset,
  isLoading,
}: AuditFilterBarProps) {
  const [draft, setDraft] = useState<AuditFilterValues>(value);
  const [endpoints, setEndpoints] = useState<string[]>([]);

  // When the applied filter changes (new search OR browser Back/forward), reset the draft.
  useEffect(() => {
    setDraft(value);
  }, [value]);

  useEffect(() => {
    const client = new Client(CookieAuth());
    client
      .getEndpointsAll()
      .then(setEndpoints)
      .catch(() => setEndpoints([]));
  }, []);

  const endpointOptions = useMemo(
    () => [
      { value: "", label: "All endpoints" },
      ...endpoints.map((endpoint) => ({ value: endpoint, label: endpoint })),
    ],
    [endpoints],
  );

  const update = <K extends keyof AuditFilterValues>(
    key: K,
    next: AuditFilterValues[K],
  ) => setDraft((d) => ({ ...d, [key]: next }));

  const handleSearch = () => onSearch(draft);
  const handleReset = () => {
    setDraft(EMPTY_AUDIT_FILTER);
    onReset();
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
            value={draft.endpointId}
            onChange={(e) => update("endpointId", e.target.value)}
            options={endpointOptions}
          />
        </div>
        <div>
          <label className="block text-xs font-medium text-muted-foreground mb-1">
            Event ID
          </label>
          <Input
            value={draft.eventId}
            onChange={(e) => update("eventId", e.target.value)}
            onKeyDown={handleKeyDown}
            placeholder="Filter by event ID..."
          />
        </div>
        <div>
          <label className="block text-xs font-medium text-muted-foreground mb-1">
            Auditor
          </label>
          <Input
            value={draft.auditorName}
            onChange={(e) => update("auditorName", e.target.value)}
            onKeyDown={handleKeyDown}
            placeholder="Filter by auditor..."
          />
        </div>
        <div>
          <label className="block text-xs font-medium text-muted-foreground mb-1">
            Event Type
          </label>
          <Input
            value={draft.eventTypeId}
            onChange={(e) => update("eventTypeId", e.target.value)}
            onKeyDown={handleKeyDown}
            placeholder="Filter by event type..."
          />
        </div>
        <div>
          <label className="block text-xs font-medium text-muted-foreground mb-1">
            Action Type
          </label>
          <select
            value={draft.auditType}
            onChange={(e) => update("auditType", e.target.value)}
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
            value={draft.createdFrom}
            onChange={(e) => update("createdFrom", e.target.value)}
            onKeyDown={handleKeyDown}
          />
        </div>
        <div>
          <label className="block text-xs font-medium text-muted-foreground mb-1">
            Created To
          </label>
          <Input
            type="datetime-local"
            value={draft.createdTo}
            onChange={(e) => update("createdTo", e.target.value)}
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
