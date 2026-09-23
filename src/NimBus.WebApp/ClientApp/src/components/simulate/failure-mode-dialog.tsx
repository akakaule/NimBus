import { useState } from "react";
import * as api from "api-client";
import { Button } from "components/ui/button";
import { Input } from "components/ui/input";
import { Select } from "components/ui/select";
import { Modal, ModalBody, ModalFooter, ModalHeader } from "components/ui/modal";
import {
  LIMITS,
  MODE_LABEL,
  SESSION_PATTERN,
  buildConfig,
  clamp,
  failureFor,
  withSubscriber,
  type PlainFailure,
} from "./simulation-utils";

const MODES: api.SimulationFailureMode[] = [
  api.SimulationFailureMode.Healthy,
  api.SimulationFailureMode.Random,
  api.SimulationFailureMode.Transient,
  api.SimulationFailureMode.Slow,
  api.SimulationFailureMode.Poison,
  api.SimulationFailureMode.NoHandler,
];

const MODE_HELP: Record<string, string> = {
  healthy: "Short delay, then complete.",
  random: "Throw a transient error with the given probability; retried, then Failed and the session blocks.",
  transient: "Throw on the first attempts, then complete on a retry.",
  slow: "Delay between the latency bounds, then complete.",
  poison: "Throw a permanent error; the message is dead-lettered without retries.",
  nohandler: "Answer Unsupported, as if no handler were registered.",
};

/**
 * Edits one owned subscriber's failure mode. Inputs clamp to the server's
 * bounds; Save PUTs the whole config with this subscriber replaced.
 */
export default function FailureModeDialog({
  status,
  endpointId,
  saving,
  onClose,
  onSave,
}: {
  status: api.SimulationStatus;
  endpointId: string;
  saving?: boolean;
  onClose: () => void;
  onSave: (config: api.SimulationConfig) => void;
}) {
  const consumes = status.endpoints?.find((e) => e.endpointId === endpointId)?.consumes ?? [];
  const [draft, setDraft] = useState<PlainFailure>(() => failureFor(status, endpointId));
  const [revert, setRevert] = useState(draft.revertAfterMinutes ? String(draft.revertAfterMinutes) : "");
  const mode = draft.mode.toLowerCase();
  const edit = (change: Partial<PlainFailure>) => setDraft((d) => ({ ...d, ...change }));
  const patternInvalid = !!draft.sessionPattern && !SESSION_PATTERN.test(draft.sessionPattern);

  const save = () => {
    const minMs = clamp(draft.latencyMinMs, LIMITS.latencyMin, LIMITS.latencyMax);
    const maxMs = Math.max(minMs, clamp(draft.latencyMaxMs, LIMITS.latencyMin, LIMITS.latencyMax));
    const revertMinutes = revert.trim() === "" ? undefined : clamp(Number(revert), LIMITS.revertMin, LIMITS.revertMax);
    const failure: PlainFailure = {
      ...draft,
      rate: clamp(draft.rate, LIMITS.rateMin, LIMITS.rateMax),
      failAttempts: clamp(draft.failAttempts, LIMITS.failAttemptsMin, LIMITS.failAttemptsMax),
      latencyMinMs: minMs,
      latencyMaxMs: maxMs,
      exceptionMessage: draft.exceptionMessage?.trim() || undefined,
      sessionPattern: draft.sessionPattern?.trim() || undefined,
      eventTypeIds: draft.eventTypeIds.filter((id) => consumes.includes(id)),
      revertAfterMinutes: revertMinutes,
    };
    onSave(buildConfig(status, { subscribers: withSubscriber(status, endpointId, failure) }));
  };

  const toggleType = (id: string, on: boolean) =>
    edit({ eventTypeIds: on ? [...draft.eventTypeIds.filter((t) => t !== id), id] : draft.eventTypeIds.filter((t) => t !== id) });

  return (
    <Modal isOpen onClose={onClose} size="lg">
      <ModalHeader onClose={onClose}>Failure mode · {endpointId}</ModalHeader>
      <ModalBody className="space-y-4 text-sm">
        <label className="block">
          Mode
          <Select
            aria-label="Mode"
            value={draft.mode}
            onChange={(e) => edit({ mode: e.target.value as api.SimulationFailureMode })}
          >
            {MODES.map((m) => (
              <option key={m} value={m}>
                {MODE_LABEL[m.toLowerCase()]}
              </option>
            ))}
          </Select>
          <span className="text-xs text-muted-foreground">{MODE_HELP[mode]}</span>
        </label>

        {mode === "random" && (
          <label className="block">
            Failure rate (%)
            <Input
              type="number"
              aria-label="Failure rate"
              min={LIMITS.rateMin}
              max={LIMITS.rateMax}
              value={draft.rate}
              onChange={(e) => edit({ rate: clamp(Number(e.target.value), LIMITS.rateMin, LIMITS.rateMax) })}
            />
          </label>
        )}
        {mode === "transient" && (
          <label className="block">
            Failing attempts
            <Input
              type="number"
              aria-label="Failing attempts"
              min={LIMITS.failAttemptsMin}
              max={LIMITS.failAttemptsMax}
              value={draft.failAttempts}
              onChange={(e) =>
                edit({ failAttempts: clamp(Number(e.target.value), LIMITS.failAttemptsMin, LIMITS.failAttemptsMax) })
              }
            />
            <span className="text-xs text-muted-foreground">At most 3, so the last retry can succeed.</span>
          </label>
        )}
        {mode === "slow" && (
          <div className="grid grid-cols-2 gap-3">
            <label>
              Latency min (ms)
              <Input
                type="number"
                aria-label="Latency min"
                min={LIMITS.latencyMin}
                max={LIMITS.latencyMax}
                value={draft.latencyMinMs}
                onChange={(e) => edit({ latencyMinMs: clamp(Number(e.target.value), LIMITS.latencyMin, LIMITS.latencyMax) })}
              />
            </label>
            <label>
              Latency max (ms)
              <Input
                type="number"
                aria-label="Latency max"
                min={LIMITS.latencyMin}
                max={LIMITS.latencyMax}
                value={draft.latencyMaxMs}
                onChange={(e) => edit({ latencyMaxMs: clamp(Number(e.target.value), LIMITS.latencyMin, LIMITS.latencyMax) })}
              />
            </label>
          </div>
        )}
        {["random", "transient", "poison", "nohandler"].includes(mode) && (
          <label className="block">
            Exception message
            <Input
              aria-label="Exception message"
              maxLength={LIMITS.exceptionMessageMax}
              placeholder="Default message for the mode"
              value={draft.exceptionMessage ?? ""}
              onChange={(e) => edit({ exceptionMessage: e.target.value })}
            />
          </label>
        )}

        <fieldset className="space-y-1">
          <legend>Event types (none selected = all)</legend>
          {consumes.map((id) => (
            <label key={id} className="flex items-center gap-2">
              <input
                type="checkbox"
                checked={draft.eventTypeIds.includes(id)}
                onChange={(e) => toggleType(id, e.target.checked)}
              />
              <span className="font-mono text-xs">{id}</span>
            </label>
          ))}
        </fieldset>

        <div className="grid grid-cols-2 gap-3">
          <label>
            Session pattern
            <Input
              aria-label="Session pattern"
              maxLength={LIMITS.sessionPatternMax}
              placeholder="sim-*-00?"
              value={draft.sessionPattern ?? ""}
              onChange={(e) => edit({ sessionPattern: e.target.value })}
            />
            {patternInvalid && (
              <span role="alert" className="text-xs text-status-danger">
                Letters, digits, - _ . : and the wildcards * ? only.
              </span>
            )}
          </label>
          <label>
            Revert to healthy after (min)
            <Input
              type="number"
              aria-label="Revert after minutes"
              min={LIMITS.revertMin}
              max={LIMITS.revertMax}
              placeholder="never"
              value={revert}
              onChange={(e) => setRevert(e.target.value)}
            />
          </label>
        </div>
      </ModalBody>
      <ModalFooter>
        <Button variant="outline" onClick={onClose}>
          Cancel
        </Button>
        <Button disabled={saving || patternInvalid} onClick={save}>
          {saving ? "Saving…" : "Apply"}
        </Button>
      </ModalFooter>
    </Modal>
  );
}
