import { useEffect, useState } from "react";
import * as api from "api-client";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "components/ui/card";
import { Input } from "components/ui/input";
import { Toggle } from "components/ui/toggle";
import { buildConfig, clamp, plainPublishers, type PlainPublisher } from "./simulation-utils";

/** Per publisher endpoint and event type: enabled and rate. Every change PUTs the whole config. */
export default function PublishersCard({
  status,
  disabled,
  onChange,
}: {
  status: api.SimulationStatus;
  disabled?: boolean;
  onChange: (config: api.SimulationConfig) => void;
}) {
  const ceiling = status.settings?.rateCeilingPerMinute ?? 600;
  const publishers = plainPublishers(status);
  // Rate inputs are edited locally and committed on blur, so a poll mid-typing
  // does not overwrite them.
  const [rates, setRates] = useState<Record<string, string>>({});
  useEffect(() => setRates({}), [status.config]);

  const commit = (next: PlainPublisher[]) => onChange(buildConfig(status, { publishers: next }));
  const update = (endpointId: string, eventTypeId: string, change: { enabled?: boolean; ratePerMinute?: number }) =>
    commit(
      publishers.map((p) =>
        p.endpointId !== endpointId
          ? p
          : { ...p, eventTypes: p.eventTypes.map((e) => (e.eventTypeId === eventTypeId ? { ...e, ...change } : e)) },
      ),
    );

  return (
    <Card>
      <CardHeader>
        <CardTitle>Publishers</CardTitle>
        <CardDescription>
          Schema-valid generated payloads. Effective rate is rate × speed, capped at {ceiling}/min overall.
        </CardDescription>
      </CardHeader>
      <CardContent className="space-y-4">
        {publishers.length === 0 && <p className="text-sm text-muted-foreground">No endpoint produces events.</p>}
        {publishers.map((publisher) => (
          <div key={publisher.endpointId}>
            <p className="mb-1 font-mono text-sm font-semibold">{publisher.endpointId}</p>
            <ul className="divide-y divide-border">
              {publisher.eventTypes.map((eventType) => {
                const key = `${publisher.endpointId}/${eventType.eventTypeId}`;
                return (
                  <li key={key} className="flex items-center justify-between gap-3 py-2">
                    <span className="min-w-0 truncate text-sm">{eventType.eventTypeId}</span>
                    <div className="flex items-center gap-3">
                      <label className="flex items-center gap-1 text-xs text-muted-foreground">
                        <Input
                          type="number"
                          aria-label={`Rate per minute for ${eventType.eventTypeId}`}
                          className="h-8 w-20"
                          min={1}
                          max={ceiling}
                          disabled={disabled}
                          value={rates[key] ?? String(eventType.ratePerMinute)}
                          onChange={(e) => setRates((r) => ({ ...r, [key]: e.target.value }))}
                          onBlur={(e) => {
                            const value = clamp(Number(e.target.value), 1, ceiling);
                            if (value !== eventType.ratePerMinute)
                              update(publisher.endpointId, eventType.eventTypeId, { ratePerMinute: value });
                            else setRates((r) => ({ ...r, [key]: String(value) }));
                          }}
                        />
                        /min
                      </label>
                      <Toggle
                        aria-label={`Publish ${eventType.eventTypeId}`}
                        checked={eventType.enabled}
                        disabled={disabled}
                        showStateLabel={false}
                        onChange={(value) => update(publisher.endpointId, eventType.eventTypeId, { enabled: value })}
                      />
                    </div>
                  </li>
                );
              })}
            </ul>
          </div>
        ))}
      </CardContent>
    </Card>
  );
}
