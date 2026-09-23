import * as api from "api-client";
import { Badge } from "components/ui/badge";
import { Button } from "components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "components/ui/card";
import { modeLabel } from "./simulation-utils";

/**
 * Consuming endpoints. Owned ones run a simulated handler whose failure mode
 * can be edited; External ones are handled by their own process and are read-only.
 */
export default function SubscribersCard({
  status,
  disabled,
  onEdit,
}: {
  status: api.SimulationStatus;
  disabled?: boolean;
  onEdit: (endpointId: string) => void;
}) {
  const consumers = (status.endpoints ?? []).filter((e) => (e.consumes?.length ?? 0) > 0);
  const owned = consumers.filter((e) => e.owned);
  const external = consumers.filter((e) => !e.owned);

  return (
    <Card>
      <CardHeader>
        <CardTitle>Subscribers</CardTitle>
        <CardDescription>Failure modes apply to simulated handlers only.</CardDescription>
      </CardHeader>
      <CardContent className="space-y-4">
        <ul className="divide-y divide-border" aria-label="Simulated subscribers">
          {owned.length === 0 && (
            <li className="py-2 text-sm text-muted-foreground">
              No endpoint is owned by the simulator. Take ownership in Admin → Simulation.
            </li>
          )}
          {owned.map((endpoint) => (
            <li key={endpoint.endpointId} className="flex items-center justify-between gap-3 py-2">
              <div className="min-w-0">
                <p className="font-mono text-sm">{endpoint.endpointId}</p>
                <p className="text-xs text-muted-foreground">{(endpoint.consumes ?? []).join(", ")}</p>
                {endpoint.liveInstanceWarning && (
                  <p className="text-xs text-status-warning">A live instance may be competing for this subscription.</p>
                )}
              </div>
              <div className="flex items-center gap-2">
                <Badge variant="primary">{modeLabel(endpoint.effectiveMode)}</Badge>
                <Button size="sm" variant="outline" disabled={disabled} onClick={() => onEdit(endpoint.endpointId ?? "")}>
                  Edit failure mode
                </Button>
              </div>
            </li>
          ))}
        </ul>
        {external.length > 0 && (
          <div>
            <p className="mb-1 font-mono text-[10px] uppercase tracking-wider text-muted-foreground">External</p>
            <ul className="divide-y divide-border" aria-label="External subscribers">
              {external.map((endpoint) => (
                <li key={endpoint.endpointId} className="flex items-center justify-between gap-3 py-2">
                  <span className="font-mono text-sm">{endpoint.endpointId}</span>
                  <span className="text-xs text-muted-foreground">handled by its own process</span>
                </li>
              ))}
            </ul>
          </div>
        )}
      </CardContent>
    </Card>
  );
}
