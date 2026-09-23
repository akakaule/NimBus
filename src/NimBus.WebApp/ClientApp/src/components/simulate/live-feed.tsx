import { Link } from "react-router-dom";
import * as api from "api-client";
import { Badge, type BadgeVariant } from "components/ui/badge";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "components/ui/card";

const OUTCOME: Record<string, { label: string; variant: BadgeVariant }> = {
  completed: { label: "Completed", variant: "completed" },
  threw: { label: "Threw", variant: "failed" },
  poisoned: { label: "Poisoned", variant: "deadlettered" },
  unsupported: { label: "Unsupported", variant: "unsupported" },
};

/**
 * The last deliveries simulated handlers saw, newest first. Handler-side
 * truth; the Resolver's status in Messages is authoritative.
 */
export default function LiveFeed({ recent }: { recent: api.SimulationDelivery[] }) {
  return (
    <Card>
      <CardHeader>
        <div className="flex flex-wrap items-baseline justify-between gap-2">
          <CardTitle>Live feed</CardTitle>
          <Link to="/Messages" className="text-sm text-primary">
            View in Messages →
          </Link>
        </div>
        <CardDescription>What simulated handlers did. Messages shows the Resolver's authoritative status.</CardDescription>
      </CardHeader>
      <CardContent>
        {recent.length === 0 ? (
          <p className="text-sm text-muted-foreground">No deliveries yet.</p>
        ) : (
          <div className="overflow-x-auto">
            <table className="w-full text-sm" aria-label="Recent deliveries">
              <thead>
                <tr className="text-left text-xs text-muted-foreground">
                  <th className="py-1 pr-3">Time</th>
                  <th className="py-1 pr-3">Endpoint</th>
                  <th className="py-1 pr-3">Event type</th>
                  <th className="py-1 pr-3">Session</th>
                  <th className="py-1 pr-3">Outcome</th>
                  <th className="py-1 pr-3">Latency</th>
                  <th className="py-1">Error</th>
                </tr>
              </thead>
              <tbody>
                {recent.map((d, index) => {
                  const outcome = OUTCOME[(d.outcome ?? "").toLowerCase()] ?? { label: d.outcome ?? "?", variant: "default" };
                  return (
                    <tr key={`${d.messageId}-${index}`} className="border-t border-border">
                      <td className="py-1 pr-3 font-mono text-xs">{d.at?.format("HH:mm:ss")}</td>
                      <td className="py-1 pr-3 font-mono text-xs">{d.endpointId}</td>
                      <td className="py-1 pr-3">{d.eventTypeId}</td>
                      <td className="py-1 pr-3 font-mono text-xs">
                        <Link to={`/Messages?sessionId=${encodeURIComponent(d.sessionId ?? "")}`} className="text-primary">
                          {d.sessionId}
                        </Link>
                      </td>
                      <td className="py-1 pr-3">
                        <Badge variant={outcome.variant}>
                          {outcome.label}
                          {(d.attempt ?? 1) > 1 || outcome.label === "Threw" ? ` (attempt ${d.attempt ?? 1})` : ""}
                        </Badge>
                      </td>
                      <td className="py-1 pr-3 tabular-nums">{d.latencyMs ?? 0} ms</td>
                      <td className="py-1 text-xs text-muted-foreground">{d.error}</td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </div>
        )}
      </CardContent>
    </Card>
  );
}
