import { useEffect, useState } from "react";
import { Link } from "react-router-dom";
import * as api from "api-client";
import { Button } from "components/ui/button";
import { Input } from "components/ui/input";
import { Toggle } from "components/ui/toggle";
import { Badge } from "components/ui/badge";
import { Modal, ModalBody, ModalFooter, ModalHeader } from "components/ui/modal";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "components/ui/card";
import {
  LIMITS,
  blockedMessage,
  clamp,
  problemMessage,
  useSimulationStatus,
} from "components/simulate/simulation-utils";

interface Draft {
  enabled: boolean;
  autoStopMinutes: number;
  rateCeilingPerMinute: number;
  ownedEndpoints: string[];
}

const toDraft = (status: api.SimulationStatus): Draft => ({
  enabled: status.settings?.enabled ?? false,
  autoStopMinutes: status.settings?.autoStopMinutes ?? 60,
  rateCeilingPerMinute: status.settings?.rateCeilingPerMinute ?? 600,
  ownedEndpoints: [...(status.settings?.ownedEndpoints ?? [])],
});

/**
 * Admin → Simulation. Switches simulate mode on, bounds it, and hands
 * consuming endpoints to the simulator. Ownership is explicit and defaults to
 * External: a simulated handler competes for the endpoint's real subscription.
 */
export default function SimulationSettings() {
  const { client, status, accept, error: loadError, loaded, refresh } = useSimulationStatus();
  const [draft, setDraft] = useState<Draft>();
  const [confirmOwn, setConfirmOwn] = useState<string>();
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string>();
  const [message, setMessage] = useState<string>();

  useEffect(() => {
    if (status && !draft) setDraft(toDraft(status));
  }, [status, draft]);

  if (!status || !draft) {
    return (
      <div className="space-y-3">
        <p role={loadError ? "alert" : "status"}>
          {loadError ?? (loaded ? "Simulation status is unavailable." : "Loading simulation settings…")}
        </p>
        {loadError && <Button onClick={() => void refresh()}>Try again</Button>}
      </div>
    );
  }

  const allowed = status.allowed ?? false;
  const disabled = !allowed || saving;
  const maxCeiling = status.maxRateCeilingPerMinute ?? 1200;
  const consumers = (status.endpoints ?? []).filter((e) => (e.consumes?.length ?? 0) > 0);
  const ownershipLocked = ["running", "pausing", "stopping"].includes((status.state ?? "").toLowerCase());

  const edit = <K extends keyof Draft>(key: K, value: Draft[K]) => {
    setDraft((current) => (current ? { ...current, [key]: value } : current));
    setMessage(undefined);
  };

  const setOwned = (endpointId: string, owned: boolean) =>
    edit(
      "ownedEndpoints",
      owned
        ? [...draft.ownedEndpoints.filter((e) => e !== endpointId), endpointId]
        : draft.ownedEndpoints.filter((e) => e !== endpointId),
    );

  async function save() {
    if (!draft) return;
    setSaving(true);
    setError(undefined);
    try {
      const next = await client.putAdminSimulationSettings(
        api.SimulationSettings.fromJS({
          enabled: draft.enabled,
          autoStopMinutes: clamp(draft.autoStopMinutes, LIMITS.autoStopMin, LIMITS.autoStopMax),
          rateCeilingPerMinute: clamp(draft.rateCeilingPerMinute, 1, maxCeiling),
          ownedEndpoints: draft.ownedEndpoints,
        }),
      );
      accept(next);
      setDraft(toDraft(next));
      setMessage("Simulation settings saved. Ownership changes apply at the next Start.");
    } catch (cause) {
      setError(problemMessage(cause));
    } finally {
      setSaving(false);
    }
  }

  const productionNames = status.productionNames ?? [];
  const allowedEnvironments = status.allowedEnvironments ?? [];

  return (
    <div className="w-full min-w-0 space-y-6">
      <header className="flex flex-wrap items-start justify-between gap-3">
        <div>
          <h2 className="text-xl font-semibold">Simulation</h2>
          <p className="text-muted-foreground">
            Drive synthetic traffic through the real platform from this WebApp. Never available in production.
          </p>
        </div>
        {allowed && status.enabled && (
          <Link to="/Simulate" className="text-sm font-semibold text-primary">
            Open Simulate →
          </Link>
        )}
      </header>

      {!allowed && (
        <p role="alert" className="rounded-md border border-status-danger/40 bg-status-danger/10 p-3 text-sm">
          {blockedMessage(status)}
        </p>
      )}
      {error && (
        <p role="alert" className="text-status-danger">
          {error}
        </p>
      )}
      {message && (
        <p role="status" className="text-status-success">
          {message}
        </p>
      )}

      <div className="grid gap-6 xl:grid-cols-[minmax(0,1.5fr)_minmax(0,1fr)]">
        <fieldset disabled={disabled} className="min-w-0 space-y-5">
          <Card>
            <CardHeader>
              <CardTitle>01 · Simulate mode</CardTitle>
              <CardDescription>State is kept in this WebApp instance and resets on restart.</CardDescription>
            </CardHeader>
            <CardContent className="space-y-4">
              <div className="flex items-center justify-between gap-4">
                <div>
                  <p className="text-sm font-semibold">Enable simulate mode</p>
                  <p className="text-xs text-muted-foreground">
                    Adds the Simulate page. Disabling stops a running simulation.
                  </p>
                </div>
                <Toggle
                  aria-label="Enable simulate mode"
                  checked={draft.enabled}
                  disabled={disabled}
                  onChange={(value) => edit("enabled", value)}
                />
              </div>
              <div className="grid gap-4 sm:grid-cols-3">
                <label className="text-sm">
                  Auto-stop (minutes)
                  <Input
                    type="number"
                    min={LIMITS.autoStopMin}
                    max={LIMITS.autoStopMax}
                    value={draft.autoStopMinutes}
                    onChange={(e) => edit("autoStopMinutes", Number(e.target.value))}
                    onBlur={(e) =>
                      edit("autoStopMinutes", clamp(Number(e.target.value), LIMITS.autoStopMin, LIMITS.autoStopMax))
                    }
                  />
                </label>
                <label className="text-sm">
                  Rate ceiling (per minute)
                  <Input
                    type="number"
                    min={1}
                    max={maxCeiling}
                    value={draft.rateCeilingPerMinute}
                    onChange={(e) => edit("rateCeilingPerMinute", Number(e.target.value))}
                    onBlur={(e) => edit("rateCeilingPerMinute", clamp(Number(e.target.value), 1, maxCeiling))}
                  />
                  <span className="text-xs text-muted-foreground">Shared by every publisher. Max {maxCeiling}.</span>
                </label>
                <label className="text-sm">
                  Session prefix
                  <Input value={status.sessionPrefix ?? ""} readOnly />
                  <span className="text-xs text-muted-foreground">Marks simulated sessions and correlations.</span>
                </label>
              </div>
            </CardContent>
          </Card>

          <Card>
            <CardHeader>
              <CardTitle>02 · Consuming endpoints</CardTitle>
              <CardDescription>
                External endpoints are published to but handled by their own process. Only endpoints the simulator
                owns get a simulated handler.
              </CardDescription>
            </CardHeader>
            <CardContent className="space-y-3">
              {ownershipLocked && (
                <p role="status" className="text-sm text-status-warning">
                  Ownership is locked while the simulation runs. Pause or stop it to change ownership.
                </p>
              )}
              {consumers.length === 0 && <p className="text-sm text-muted-foreground">No consuming endpoints.</p>}
              <ul className="divide-y divide-border">
                {consumers.map((endpoint) => {
                  const id = endpoint.endpointId ?? "";
                  const owned = draft.ownedEndpoints.includes(id);
                  return (
                    <li key={id} className="flex flex-wrap items-center justify-between gap-3 py-3">
                      <div className="min-w-0">
                        <p className="font-mono text-sm">{id}</p>
                        <p className="text-xs text-muted-foreground">Consumes {(endpoint.consumes ?? []).join(", ")}</p>
                        {endpoint.liveInstanceWarning && (
                          <p className="text-xs text-status-warning">
                            A live instance answered a heartbeat recently and may be competing for this subscription.
                          </p>
                        )}
                      </div>
                      <div className="flex items-center gap-3">
                        <Badge variant={owned ? "primary" : "secondary"}>{owned ? "Simulated" : "External"}</Badge>
                        <label className="flex items-center gap-2 text-sm">
                          <input
                            type="checkbox"
                            aria-label={`Simulator owns ${id}`}
                            checked={owned}
                            disabled={disabled || ownershipLocked}
                            onChange={(e) => (e.target.checked ? setConfirmOwn(id) : setOwned(id, false))}
                          />
                          Simulator owns this endpoint
                        </label>
                      </div>
                    </li>
                  );
                })}
              </ul>
            </CardContent>
          </Card>
        </fieldset>

        <aside className="min-w-0 space-y-5">
          <Card>
            <CardHeader>
              <CardTitle>Environment policy</CardTitle>
              <CardDescription>
                Current environment: <span className="font-mono">{status.environment?.trim() || "(not set)"}</span>
              </CardDescription>
            </CardHeader>
            <CardContent>
              <table className="w-full text-sm">
                <thead>
                  <tr className="text-left text-xs text-muted-foreground">
                    <th className="py-1">Environment</th>
                    <th className="py-1">Simulation</th>
                  </tr>
                </thead>
                <tbody>
                  {allowedEnvironments.map((env) => (
                    <tr key={`allowed-${env}`}>
                      <td className="py-1 font-mono">{env}</td>
                      <td className="py-1 text-status-success">allowed</td>
                    </tr>
                  ))}
                  {productionNames.map((env) => (
                    <tr key={`prod-${env}`}>
                      <td className="py-1 font-mono">{env}</td>
                      <td className="py-1 text-status-danger">permanently blocked</td>
                    </tr>
                  ))}
                  <tr>
                    <td className="py-1 font-mono">anything else</td>
                    <td className="py-1 text-muted-foreground">blocked</td>
                  </tr>
                </tbody>
              </table>
            </CardContent>
          </Card>
        </aside>
      </div>

      <footer className="flex flex-wrap justify-between gap-3 border-t pt-4">
        <p className="text-sm text-muted-foreground">Site Owner only · audited · takes effect on this instance</p>
        <div className="flex gap-2">
          <Button type="button" variant="outline" disabled={saving} onClick={() => setDraft(toDraft(status))}>
            Reset
          </Button>
          <Button type="button" disabled={disabled} onClick={() => void save()}>
            {saving ? "Saving…" : "Save simulation settings"}
          </Button>
        </div>
      </footer>

      <Modal isOpen={confirmOwn !== undefined} onClose={() => setConfirmOwn(undefined)} size="md">
        <ModalHeader onClose={() => setConfirmOwn(undefined)}>Hand {confirmOwn} to the simulator?</ModalHeader>
        <ModalBody className="space-y-2 text-sm">
          <p>
            The simulator will host a handler on <span className="font-mono">{confirmOwn}</span>'s real subscription.
            If the endpoint's own process is running, both compete for the same messages and the results mix.
          </p>
          <p>Only do this when the real subscriber is not running. The change applies at the next Start.</p>
        </ModalBody>
        <ModalFooter>
          <Button variant="outline" onClick={() => setConfirmOwn(undefined)}>
            Cancel
          </Button>
          <Button
            onClick={() => {
              if (confirmOwn) setOwned(confirmOwn, true);
              setConfirmOwn(undefined);
            }}
          >
            Take ownership
          </Button>
        </ModalFooter>
      </Modal>
    </div>
  );
}
