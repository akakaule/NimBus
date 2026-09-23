import * as api from "api-client";
import { Button } from "components/ui/button";
import { PRESETS, buildConfig, presetSubscribers, type Preset } from "./simulation-utils";

/**
 * Client-side scenario presets. Each applies one failure mode to every owned
 * subscriber in a single PUT of the whole config; External endpoints are never touched.
 */
export default function ScenarioBar({
  status,
  disabled,
  onApply,
}: {
  status: api.SimulationStatus;
  disabled?: boolean;
  onApply: (config: api.SimulationConfig) => void;
}) {
  const hasOwned = (status.endpoints ?? []).some((e) => e.owned && (e.consumes?.length ?? 0) > 0);
  const apply = (preset: Preset) =>
    onApply(buildConfig(status, { subscribers: presetSubscribers(status, preset) }));

  return (
    <section aria-label="Scenarios" className="flex flex-wrap items-center gap-2">
      <span className="font-mono text-[10px] uppercase tracking-wider text-muted-foreground">Scenario</span>
      {PRESETS.map((preset) => (
        <Button
          key={preset.id}
          size="sm"
          variant="outline"
          title={preset.description}
          disabled={disabled || !hasOwned}
          onClick={() => apply(preset)}
        >
          {preset.label}
        </Button>
      ))}
      {!hasOwned && (
        <span className="text-xs text-muted-foreground">
          Scenarios change simulated handlers only. Take ownership of an endpoint in Admin → Simulation.
        </span>
      )}
    </section>
  );
}
