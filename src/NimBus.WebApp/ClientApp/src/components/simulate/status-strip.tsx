import * as api from "api-client";
import { Badge, type BadgeVariant } from "components/ui/badge";
import { formatMoment } from "functions/endpoint.functions";

const STATE_VARIANT: Record<string, BadgeVariant> = {
  running: "success",
  pausing: "warning",
  paused: "warning",
  stopping: "warning",
  stopped: "secondary",
};

function Stat({ label, value, tone }: { label: string; value: number | string; tone?: string }) {
  return (
    <div className="min-w-[110px] rounded-md border border-border bg-card px-3 py-2">
      <p className="font-mono text-[10px] uppercase tracking-wider text-muted-foreground">{label}</p>
      <p className={`text-lg font-semibold tabular-nums ${tone ?? ""}`}>{value}</p>
    </div>
  );
}

/** Run state, timing, counters and the ceiling indicator. Counters are handler-side truth. */
export default function StatusStrip({ status }: { status: api.SimulationStatus }) {
  const state = (status.state ?? "stopped").toLowerCase();
  const counters = status.counters;
  const ceiling = status.settings?.rateCeilingPerMinute ?? 0;

  return (
    <section aria-label="Simulation status" className="space-y-3">
      <div className="flex flex-wrap items-center gap-3 text-sm">
        <Badge variant={STATE_VARIANT[state] ?? "secondary"} data-testid="simulation-state">
          {state}
        </Badge>
        {status.startedAt && <span className="text-muted-foreground">Started {formatMoment(status.startedAt)}</span>}
        {status.autoStopAt && <span className="text-muted-foreground">Auto-stop {formatMoment(status.autoStopAt)}</span>}
        {status.capped && (
          <Badge variant="warning" role="status">
            capped at {ceiling}/min
          </Badge>
        )}
      </div>
      <div className="flex flex-wrap gap-2">
        <Stat label="Published" value={counters?.published ?? 0} />
        <Stat label="Handled OK" value={counters?.handledOk ?? 0} tone="text-status-success" />
        <Stat label="Handler errors" value={counters?.handlerErrors ?? 0} tone="text-status-danger" />
        <Stat label="Poisoned" value={counters?.poisoned ?? 0} tone="text-status-danger" />
        <Stat label="Publish errors" value={counters?.publishErrors ?? 0} />
        <Stat label="Abandoned sends" value={counters?.abandonedSends ?? 0} />
        <Stat label="Throughput" value={`${counters?.throughputPerMinute ?? 0}/min`} />
      </div>
    </section>
  );
}
