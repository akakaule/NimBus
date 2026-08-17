import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import * as api from "api-client";
import Page from "components/page";
import {
  Card,
  CardContent,
  CardDescription,
  CardHeader,
  CardTitle,
} from "components/ui/card";
import { Button } from "components/ui/button";
import { Badge } from "components/ui/badge";
import { EmptyState } from "components/ui/empty-state";
import { Spinner } from "components/ui/spinner";
import { StatRow, StatTile } from "components/ui/stat-tile";
import {
  normalizeDayState,
  normalizeLiveness,
  normalizeStatus,
  statusHints,
  statusVariants,
} from "components/admin/heartbeat-status";
import { subscribeHeartbeatUpdates } from "lib/grid-events-connection";
import { cn } from "lib/utils";

const WINDOWS = [7, 30, 90] as const;

const dayTone = {
  none: "bg-muted border-border",
  full: "bg-status-success/70 border-status-success",
  partial: "bg-status-warning/70 border-status-warning",
  gap: "bg-status-danger/80 border-status-danger",
} as const;

function percent(value?: number): string {
  return value == null ? "—" : `${(value * 100).toFixed(1)}%`;
}

function duration(seconds?: number): string {
  if (seconds == null) return "—";
  if (seconds >= 86400) return `${Math.round(seconds / 86400)}d`;
  if (seconds >= 3600) return `${(seconds / 3600).toFixed(1)}h`;
  return `${Math.max(1, Math.round(seconds / 60))}m`;
}

export default function Heartbeat() {
  const [windowDays, setWindowDays] = useState<(typeof WINDOWS)[number]>(30);
  const [data, setData] = useState<api.HeartbeatPage | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const requestId = useRef(0);
  const client = useMemo(() => new api.Client(api.CookieAuth()), []);

  const load = useCallback(
    async (days: number) => {
      const current = ++requestId.current;
      setLoading(true);
      try {
        const next = await client.getHeartbeatPage(days);
        if (current !== requestId.current) return;
        setData(next);
        setError(null);
      } catch (reason: unknown) {
        if (current !== requestId.current) return;
        setError(
          reason instanceof Error
            ? reason.message
            : "Failed to load heartbeat history",
        );
      } finally {
        if (current === requestId.current) setLoading(false);
      }
    },
    [client],
  );

  useEffect(() => {
    void load(windowDays);
  }, [load, windowDays]);

  useEffect(() => {
    const subscription = subscribeHeartbeatUpdates(
      () => void load(windowDays),
      () => undefined,
    );
    return () => subscription.dispose();
  }, [load, windowDays]);

  return (
    <Page
      title="Heartbeat"
      subtitle="Fleet reachability, observed uptime, and recent silent periods"
      actions={
        <div className="flex gap-1" aria-label="Heartbeat history window">
          {WINDOWS.map((days) => (
            <Button
              key={days}
              size="sm"
              variant={windowDays === days ? "solid" : "outline"}
              colorScheme={windowDays === days ? "primary" : "gray"}
              onClick={() => setWindowDays(days)}
            >
              {days}d
            </Button>
          ))}
        </div>
      }
    >
      <div className="w-full space-y-5">
        {error && (
          <div
            role="alert"
            className="rounded-nb-md border border-status-danger/30 bg-status-danger-50 p-4 text-status-danger-ink"
          >
            {error}
          </div>
        )}

        {loading && !data ? (
          <div className="flex min-h-64 items-center justify-center">
            <Spinner />
          </div>
        ) : data ? (
          <>
            <StatRow className="max-lg:grid-cols-2 max-sm:grid-cols-1">
              <StatTile
                label="Reporting now"
                value={`${data.adaptersReporting ?? 0}/${data.adaptersTotal ?? 0}`}
              />
              <StatTile
                label={`Fleet uptime · ${windowDays}d`}
                value={percent(data.fleetUptime)}
                delta="Weighted by probes sent"
              />
              <StatTile
                label="Missed · UTC today"
                value={data.missedBeatsToday ?? 0}
                tone={(data.missedBeatsToday ?? 0) > 0 ? "warning" : "default"}
              />
              <StatTile
                label="Longest recent gap"
                value={duration(data.longestGap)}
                tone={(data.longestGap ?? 0) >= 3600 ? "danger" : "muted"}
              />
            </StatRow>

            <Card>
              <CardHeader>
                <CardTitle>Adapter status</CardTitle>
                <CardDescription>
                  Daily cells are UTC calendar days. Green requires at least 90%
                  observation coverage; amber can mean misses or incomplete
                  coverage.
                </CardDescription>
              </CardHeader>
              <CardContent className="p-0 overflow-x-auto">
                {(data.adapters?.length ?? 0) === 0 ? (
                  <EmptyState
                    title="No adapters"
                    description="No endpoints are currently included in heartbeat probing."
                  />
                ) : (
                  <table className="w-full min-w-[900px] text-sm">
                    <thead>
                      <tr className="border-b bg-muted">
                        <th className="p-3 text-left">Endpoint</th>
                        <th className="p-3 text-left">Status</th>
                        <th className="p-3 text-left">Uptime</th>
                        <th className="p-3 text-left">SDK</th>
                        <th className="p-3 text-left">Daily history</th>
                      </tr>
                    </thead>
                    <tbody>
                      {(data.adapters ?? []).map((adapter) => {
                        const status = normalizeStatus(adapter.status);
                        const liveness = normalizeLiveness(adapter.liveness);
                        return (
                          <tr
                            key={adapter.endpointId}
                            className="border-b last:border-0"
                          >
                            <td className="p-3 font-medium">
                              {adapter.endpointId}
                            </td>
                            <td className="p-3">
                              <Badge
                                variant={statusVariants[status]}
                                title={statusHints[status]}
                              >
                                {liveness === "alive" ? status : liveness}
                              </Badge>
                            </td>
                            <td className="p-3 font-mono text-xs">
                              {percent(adapter.uptime)}
                            </td>
                            <td className="p-3 font-mono text-xs">
                              {adapter.sdkVersion ||
                                (status === "Unsupported"
                                  ? "pre-heartbeat SDK"
                                  : "unknown")}
                            </td>
                            <td className="p-3">
                              <div className="flex gap-1">
                                {(adapter.days ?? []).map((day) => {
                                  const state = normalizeDayState(day.state);
                                  const label =
                                    day.dayUtc?.format("DD MMM") ??
                                    "unknown day";
                                  return (
                                    <span
                                      key={day.dayUtc?.toISOString() ?? label}
                                      aria-label={`${label}: ${state}`}
                                      title={`${label}: ${state}; ${Math.round((day.coverage ?? 0) * 24)}h observed; ${day.missed ?? 0} missed`}
                                      className={cn(
                                        "h-5 w-2.5 shrink-0 rounded-sm border",
                                        dayTone[state],
                                      )}
                                    />
                                  );
                                })}
                              </div>
                            </td>
                          </tr>
                        );
                      })}
                    </tbody>
                  </table>
                )}
              </CardContent>
            </Card>

            <Card>
              <CardHeader>
                <CardTitle>Recent gaps</CardTitle>
                <CardDescription>
                  Actual elapsed duration; ongoing gaps update on refresh.
                </CardDescription>
              </CardHeader>
              <CardContent className="p-0">
                {(data.gaps?.length ?? 0) === 0 ? (
                  <EmptyState
                    title="No recent gaps"
                    description="No heartbeat outage overlaps this window."
                  />
                ) : (
                  <div className="divide-y">
                    {(data.gaps ?? []).map((gap) => (
                      <div
                        key={`${gap.endpointId}-${gap.fromUtc?.toISOString()}`}
                        className="flex items-center gap-4 p-4"
                      >
                        <span className="font-medium flex-1">
                          {gap.endpointId}
                        </span>
                        <span className="font-mono text-xs">
                          {gap.fromUtc?.format("DD MMM HH:mm")} →{" "}
                          {gap.toUtc?.format("DD MMM HH:mm") ?? "now"}
                        </span>
                        <span className="font-mono text-xs">
                          {duration(gap.durationSeconds)}
                        </span>
                        {gap.ongoing && <Badge variant="error">ongoing</Badge>}
                        {gap.cause && (
                          <span className="text-xs text-muted-foreground">
                            {gap.cause}
                          </span>
                        )}
                      </div>
                    ))}
                  </div>
                )}
              </CardContent>
            </Card>
          </>
        ) : null}
      </div>
    </Page>
  );
}
