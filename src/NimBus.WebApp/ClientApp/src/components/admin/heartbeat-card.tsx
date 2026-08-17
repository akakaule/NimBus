import { useCallback, useEffect, useMemo, useState } from "react";
import { Link } from "react-router-dom";
import * as api from "api-client";
import { Button } from "components/ui/button";
import { Checkbox } from "components/ui/checkbox";
import { Input } from "components/ui/input";
import {
  Card,
  CardContent,
  CardDescription,
  CardHeader,
  CardTitle,
} from "components/ui/card";
import { subscribeHeartbeatUpdates } from "lib/grid-events-connection";
import { formatMoment } from "functions/endpoint.functions";

/** Fallback poll cadence while the hub is down (see PlatformServicesCard). */
const POLL_MS = 30_000;

const DEFAULT_INTERVAL_SECONDS = 300;
const DEFAULT_TIMEOUT_SECONDS = 60;

/**
 * Admin → Health, second card. The scheduled endpoint fan-out: its schedule,
 * a manual send, and the per-endpoint answer table. Adapters answer the probe
 * automatically from the SDK, so a row that never leaves Pending means the
 * endpoint is not draining its subscription.
 */
export default function HeartbeatCard() {
  const [settings, setSettings] = useState<api.HeartbeatSettings | null>(null);
  const [overview, setOverview] = useState<api.HeartbeatOverviewRow[]>([]);
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [sending, setSending] = useState(false);
  const [sentCount, setSentCount] = useState<number | null>(null);
  const [busy, setBusy] = useState<Record<string, boolean>>({});
  const [error, setError] = useState<string | null>(null);
  const [live, setLive] = useState(false);

  const client = useMemo(() => new api.Client(api.CookieAuth()), []);

  const loadOverview = useCallback(async () => {
    setOverview(await client.getAdminHeartbeatOverview());
  }, [client]);

  const loadAll = useCallback(async () => {
    try {
      const [nextSettings, nextOverview] = await Promise.all([
        client.getAdminHeartbeatSettings(),
        client.getAdminHeartbeatOverview(),
      ]);
      setSettings(nextSettings);
      setOverview(nextOverview);
      setError(null);
    } catch (err: unknown) {
      // Without this the table falls back to its empty state and a 500 is
      // indistinguishable from "this platform has no endpoints".
      setError(
        err instanceof Error ? err.message : "Failed to load heartbeat state",
      );
    } finally {
      setLoading(false);
    }
  }, [client]);

  useEffect(() => {
    void loadAll();
  }, [loadAll]);

  useEffect(() => {
    const subscription = subscribeHeartbeatUpdates(
      () => {
        void loadOverview().catch(() => undefined);
      },
      (state) => setLive(state === "connected"),
    );
    return () => subscription.dispose();
  }, [loadOverview]);

  useEffect(() => {
    if (live) return;
    const handle = window.setInterval(() => {
      void loadOverview().catch(() => undefined);
    }, POLL_MS);
    return () => window.clearInterval(handle);
  }, [live, loadOverview]);

  function patchSettings(patch: Partial<api.IHeartbeatSettings>) {
    setSettings(
      (old) => new api.HeartbeatSettings({ ...(old ?? {}), ...patch }),
    );
  }

  // Posted exactly as typed — the server owns the clamps (interval ≥ 30s,
  // timeout in [5s, interval]) and returns the values it actually stored, so
  // an out-of-range entry snaps back visibly instead of being silently edited
  // under the operator's cursor.
  async function saveSettings() {
    if (!settings) return;
    setSaving(true);
    try {
      const saved = await client.putAdminHeartbeatSettings(settings);
      setSettings(saved);
      setError(null);
    } catch (err: unknown) {
      setError(
        err instanceof Error
          ? err.message
          : "Failed to save heartbeat settings",
      );
    } finally {
      setSaving(false);
    }
  }

  async function sendNow() {
    setSending(true);
    setSentCount(null);
    try {
      const result = await client.postAdminHeartbeatSend();
      setSentCount(result.count ?? 0);
      await loadAll();
    } catch (err: unknown) {
      setError(err instanceof Error ? err.message : "Failed to send heartbeat");
    } finally {
      setSending(false);
    }
  }

  async function setEndpointEnabled(endpointId: string, enabled: boolean) {
    setBusy((prev) => ({ ...prev, [endpointId]: true }));
    try {
      await client.putAdminHeartbeatEndpointEnabled(
        endpointId,
        new api.HeartbeatEndpointEnabledRequest({ enabled }),
      );
      await loadOverview();
      setError(null);
    } catch (err: unknown) {
      setError(
        err instanceof Error
          ? err.message
          : `Failed to update heartbeat for ${endpointId}`,
      );
    } finally {
      setBusy((prev) => ({ ...prev, [endpointId]: false }));
    }
  }

  const enabled = settings?.enabled ?? false;

  return (
    <Card>
      <CardHeader>
        <div className="flex items-start justify-between gap-4">
          <div className="space-y-1.5">
            <CardTitle>Endpoint heartbeat</CardTitle>
            <CardDescription>
              Configure the endpoint probe schedule and inclusion list. Current
              liveness, uptime, SDK versions, and gaps are on the{" "}
              <Link className="text-primary hover:underline" to="/Heartbeat">
                Heartbeat page
              </Link>
              .
            </CardDescription>
          </div>
          <Button
            variant="ghost"
            colorScheme="gray"
            size="sm"
            onClick={() => void loadAll()}
            disabled={loading}
          >
            Refresh
          </Button>
        </div>
      </CardHeader>
      <CardContent className="p-0">
        {error && (
          <div
            role="alert"
            className="m-4 bg-status-danger-50 border border-status-danger/30 dark:bg-red-950/30 dark:border-red-900/60 rounded-nb-md p-4 text-status-danger-ink dark:text-red-200"
          >
            {error}
          </div>
        )}

        <div className="p-4 space-y-3 border-b border-border">
          <div className="flex flex-wrap items-end gap-4">
            <div className="pb-2">
              <Checkbox
                checked={enabled}
                disabled={loading || saving}
                onChange={(event) =>
                  patchSettings({ enabled: event.currentTarget.checked })
                }
                label="Scheduled heartbeat enabled"
              />
            </div>

            <label className="text-xs font-medium text-muted-foreground">
              Interval seconds
              <Input
                type="number"
                min={30}
                className="mt-1 w-40"
                value={settings?.intervalSeconds ?? DEFAULT_INTERVAL_SECONDS}
                disabled={loading || saving}
                onChange={(event) =>
                  patchSettings({
                    intervalSeconds: Number(event.currentTarget.value),
                  })
                }
              />
            </label>

            <label className="text-xs font-medium text-muted-foreground">
              Timeout seconds
              <Input
                type="number"
                min={5}
                className="mt-1 w-40"
                value={settings?.timeoutSeconds ?? DEFAULT_TIMEOUT_SECONDS}
                disabled={loading || saving}
                onChange={(event) =>
                  patchSettings({
                    timeoutSeconds: Number(event.currentTarget.value),
                  })
                }
              />
            </label>

            <Button
              variant="outline"
              colorScheme="primary"
              onClick={() => void saveSettings()}
              disabled={!settings || saving}
              isLoading={saving}
            >
              Save
            </Button>

            <Button
              colorScheme="primary"
              onClick={() => void sendNow()}
              disabled={sending}
              isLoading={sending}
            >
              Send heartbeat now
            </Button>
          </div>

          <p className="text-xs text-muted-foreground">
            Interval is clamped to a minimum of 30 seconds and timeout to a
            minimum of 5 seconds (and never above the interval). Last scheduled
            send:{" "}
            {settings?.lastSentAtUtc
              ? formatMoment(settings.lastSentAtUtc)
              : "never"}
            .
          </p>

          {sentCount !== null && (
            <p className="text-xs text-muted-foreground">
              Heartbeat sent to {sentCount} endpoint(s).
            </p>
          )}
        </div>

        <div className="overflow-x-auto">
          <table className="w-full text-sm">
            <thead>
              <tr className="border-b bg-muted">
                <th className="text-left p-3 font-medium">Endpoint</th>
                <th className="text-left p-3 font-medium">Included</th>
              </tr>
            </thead>
            <tbody>
              {overview.map((row) => {
                // Null means "never configured either way", which is included.
                const included = row.isHeartbeatEnabled !== false;
                const endpointId = row.endpointId ?? "";
                const isBusy = busy[endpointId] ?? false;
                return (
                  <tr
                    key={endpointId}
                    className="border-b border-border/50 last:border-b-0"
                  >
                    <td className="p-3 font-medium">{endpointId}</td>
                    <td className="p-3">
                      <Button
                        size="xs"
                        variant="outline"
                        colorScheme={included ? "gray" : "green"}
                        isLoading={isBusy}
                        disabled={endpointId === ""}
                        onClick={() =>
                          void setEndpointEnabled(endpointId, !included)
                        }
                      >
                        {included ? "Exclude" : "Include"}
                      </Button>
                    </td>
                  </tr>
                );
              })}
              {overview.length === 0 && (
                <tr>
                  <td
                    colSpan={2}
                    className="p-6 text-center text-muted-foreground"
                  >
                    {/* Never claim "no endpoints" when the load failed — the
                        banner above already says what actually happened. */}
                    {loading ? "Loading…" : error ? "—" : "No endpoints."}
                  </td>
                </tr>
              )}
            </tbody>
          </table>
        </div>
      </CardContent>
    </Card>
  );
}
