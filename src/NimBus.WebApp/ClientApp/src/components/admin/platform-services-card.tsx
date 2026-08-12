import { useCallback, useEffect, useMemo, useState } from "react";
import * as api from "api-client";
import { Button } from "components/ui/button";
import { Badge } from "components/ui/badge";
import {
  Card,
  CardContent,
  CardDescription,
  CardHeader,
  CardTitle,
} from "components/ui/card";
import { subscribeServiceHealthUpdates } from "lib/grid-events-connection";
import { formatMoment } from "functions/endpoint.functions";
import { normalizeStatus, statusHints, statusVariants } from "./heartbeat-status";

/** Fallback poll cadence while the hub is down — mirrors the Flow page's
 *  degraded-mode posture: live updates when connected, polling otherwise. */
const POLL_MS = 30_000;

// Blurb per service, so the row explains what "Off" would actually mean.
const serviceDescriptions: Record<string, string> = {
  Resolver:
    "Persists every message outcome. While it is down, endpoint heartbeats and event state stop updating.",
};

/**
 * Admin → Health, top card. Liveness of NimBus's own services, measured by a
 * round-trip probe over Service Bus — a service only answers if it is running
 * AND draining its subscription, which no HTTP health check can tell you.
 */
export default function PlatformServicesCard() {
  const [rows, setRows] = useState<api.ServiceHealthRow[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [live, setLive] = useState(false);

  const client = useMemo(() => new api.Client(api.CookieAuth()), []);

  const load = useCallback(async () => {
    try {
      setRows(await client.getAdminHealthServices());
      setError(null);
    } catch (err: unknown) {
      // A failed load must not read as "no services" — the banner says what
      // actually happened and the table falls back to a neutral placeholder.
      setError(
        err instanceof Error ? err.message : "Failed to load service health",
      );
    } finally {
      setLoading(false);
    }
  }, [client]);

  useEffect(() => {
    void load();
  }, [load]);

  useEffect(() => {
    const subscription = subscribeServiceHealthUpdates(
      () => {
        void load();
      },
      (state) => setLive(state === "connected"),
    );
    return () => subscription.dispose();
  }, [load]);

  // Only while degraded: with the hub connected every settled probe already
  // pushes an update, so polling would just duplicate it.
  useEffect(() => {
    if (live) return;
    const handle = window.setInterval(() => {
      void load();
    }, POLL_MS);
    return () => window.clearInterval(handle);
  }, [live, load]);

  return (
    <Card>
      <CardHeader>
        <div className="flex items-start justify-between gap-4">
          <div className="space-y-1.5">
            <CardTitle>Platform services</CardTitle>
            <CardDescription>
              Liveness of NimBus's own services, measured by a round-trip probe
              over Service Bus — a service only answers if it is running and
              draining its subscription. Probed on the heartbeat interval,
              independent of the endpoint heartbeat switch below.
            </CardDescription>
          </div>
          <Button
            variant="ghost"
            colorScheme="gray"
            size="sm"
            onClick={() => void load()}
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
        <div className="overflow-x-auto">
          <table className="w-full text-sm">
            <thead>
              <tr className="border-b bg-muted">
                <th className="text-left p-3 font-medium">Service</th>
                <th className="text-left p-3 font-medium">Status</th>
                <th className="text-left p-3 font-medium">Round trip</th>
                <th className="text-left p-3 font-medium">Version</th>
                <th className="text-left p-3 font-medium">Last seen</th>
                <th className="text-left p-3 font-medium">Last probe</th>
              </tr>
            </thead>
            <tbody>
              {rows.map((row) => {
                const status = normalizeStatus(row.status);
                return (
                  <tr
                    key={row.serviceId}
                    className="border-b border-border/50 last:border-b-0 align-top"
                  >
                    <td className="p-3">
                      <span className="font-medium">{row.serviceId}</span>
                      {row.serviceId && serviceDescriptions[row.serviceId] && (
                        <p className="mt-0.5 text-xs text-muted-foreground">
                          {serviceDescriptions[row.serviceId]}
                        </p>
                      )}
                    </td>
                    <td className="p-3">
                      <div className="flex items-center gap-2">
                        <Badge
                          variant={statusVariants[status]}
                          title={statusHints[status]}
                        >
                          {status}
                        </Badge>
                        {row.probeInFlight && (
                          <span className="text-xs text-muted-foreground">
                            probing…
                          </span>
                        )}
                      </div>
                    </td>
                    <td className="p-3 font-mono text-xs">
                      {row.roundTripMs == null ? "—" : `${row.roundTripMs} ms`}
                    </td>
                    <td className="p-3 font-mono text-xs">
                      {row.version || "unknown"}
                    </td>
                    <td className="p-3 text-xs text-muted-foreground">
                      {row.lastSeenUtc ? formatMoment(row.lastSeenUtc) : "—"}
                    </td>
                    <td className="p-3 text-xs text-muted-foreground">
                      {row.lastProbeSentUtc
                        ? formatMoment(row.lastProbeSentUtc)
                        : "never"}
                    </td>
                  </tr>
                );
              })}
              {rows.length === 0 && (
                <tr>
                  <td
                    colSpan={6}
                    className="p-6 text-center text-muted-foreground"
                  >
                    {/* Never claim "no services" when the load failed — the
                        banner above already says what actually happened. */}
                    {loading ? "Loading…" : error ? "—" : "No services."}
                  </td>
                </tr>
              )}
            </tbody>
          </table>
        </div>
        <p className="p-3 text-xs text-muted-foreground">
          <span className="font-semibold">Unknown</span> means no probe has
          settled yet — allow one heartbeat interval after startup.
        </p>
      </CardContent>
    </Card>
  );
}
