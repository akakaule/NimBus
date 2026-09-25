import * as React from "react";
import * as api from "api-client";
import { Link } from "react-router-dom";
import Page from "components/page";
import DataTable, {
  ITableBodyAction,
  ITableHeadAction,
  ITableHeadCell,
  ITableRow,
} from "components/data-table";
import ColumnChooser from "components/data-table/column-chooser";
import TruncatedGuid from "components/common/truncated-guid";
import { Badge } from "components/ui/badge";
import { Checkbox } from "components/ui/checkbox";
import { EmptyState } from "components/ui/empty-state";
import { StatRow, StatTile } from "components/ui/stat-tile";
import FailedHistogram from "components/failed-messages/failed-histogram";
import FailedFilterBar from "components/failed-messages/failed-filter-bar";
import FailedErrorGroupsView from "components/failed-messages/failed-error-groups";
import { useUrlFilters } from "hooks/use-url-filters";
import { formatMoment } from "functions/endpoint.functions";
import { notifyError, notifySuccess } from "functions/notifications.functions";
import {
  EMPTY_FAILED_FILTER,
  PERIOD_OPTIONS,
  SEARCH_FIELDS,
  deferredCountsBySession,
  errorTextOf,
  failureBacklog,
  formatBucket,
  periodOption,
  resolveWindow,
  selectedBucketWindow,
  toFailedSearchFilter,
  type FailedFilterValues,
} from "functions/failed-messages.functions";
import { cn } from "lib/utils";

const PAGE_SIZE = 100;

// DataTable applies numeric widths only (a width of exactly 150 means "unset"). Every column
// but "Event type / last error" is sized, so the error gets the remaining width.
const COLUMNS: (ITableHeadCell & { locked?: boolean })[] = [
  {
    id: "eventId",
    label: "Event Id",
    numeric: false,
    width: 104,
    locked: true,
  },
  { id: "status", label: "Status", numeric: false, width: 170 },
  { id: "endpoint", label: "Endpoint", numeric: false, width: 160 },
  { id: "sessionId", label: "Session Id", numeric: false, width: 104 },
  { id: "error", label: "Event type / last error", numeric: false },
  { id: "resubmitCount", label: "Resubmits", numeric: true, width: 96 },
  { id: "reported", label: "Reported", numeric: false, width: 104 },
  { id: "updated", label: "Updated", numeric: false, width: 140 },
  { id: "added", label: "Added", numeric: false, width: 140 },
];

// Column choices are a personal display preference, persisted like the endpoint page's.
const HIDDEN_COLUMNS_KEY = "failed-messages:hidden-columns";
const DEFAULT_HIDDEN = ["added"];

function loadHiddenColumns(): Set<string> {
  try {
    const raw = window.localStorage?.getItem(HIDDEN_COLUMNS_KEY);
    const parsed = raw ? (JSON.parse(raw) as unknown) : DEFAULT_HIDDEN;
    return new Set(
      Array.isArray(parsed)
        ? parsed.filter((x): x is string => typeof x === "string")
        : DEFAULT_HIDDEN,
    );
  } catch {
    return new Set(DEFAULT_HIDDEN);
  }
}

function saveHiddenColumns(hidden: Set<string>): void {
  try {
    window.localStorage?.setItem(
      HIDDEN_COLUMNS_KEY,
      JSON.stringify([...hidden]),
    );
  } catch {
    // Storage can be unavailable in hardened browsers or test runners.
  }
}

const statusVariant = (status: string | undefined) =>
  status === api.ResolutionStatus.DeadLettered
    ? "deadlettered"
    : status === api.ResolutionStatus.Unsupported
      ? "unsupported"
      : "failed";

type View = "list" | "endpoint" | "error";

// Only the search fields and the range drive data; view, split and the selected bar are
// presentation (the selected bar narrows the list, but never the chart).
const searchKey = (v: FailedFilterValues) =>
  JSON.stringify([v.period, ...SEARCH_FIELDS.map((f) => v[f])]);

export default function FailedMessages() {
  const { applied, applyFilters, resetFilters } =
    useUrlFilters<FailedFilterValues>(EMPTY_FAILED_FILTER, {
      persistKey: "nimbus.failed.filters",
    });
  const view = (
    ["list", "endpoint", "error"].includes(applied.view) ? applied.view : "list"
  ) as View;
  const split = applied.split === "endpoint" ? "endpoint" : "status";
  const client = React.useMemo(() => new api.Client(api.CookieAuth()), []);

  const [histogram, setHistogram] = React.useState<api.FailedHistogram>();
  const [histogramLoading, setHistogramLoading] = React.useState(true);
  const [events, setEvents] = React.useState<api.Event[]>([]);
  const [continuationToken, setContinuationToken] = React.useState<string>();
  const [listLoading, setListLoading] = React.useState(false);
  const [errorGroups, setErrorGroups] = React.useState<api.FailedErrorGroups>();
  const [groupsLoading, setGroupsLoading] = React.useState(false);
  const [blocked, setBlocked] = React.useState<Record<string, number>>({});
  const [backlog, setBacklog] = React.useState<number>();
  const [hideReported, setHideReported] = React.useState(false);
  const [hiddenColumns, setHiddenColumns] =
    React.useState<Set<string>>(loadHiddenColumns);
  const headCells = React.useMemo(
    () => COLUMNS.filter((c) => c.locked || !hiddenColumns.has(c.id)),
    [hiddenColumns],
  );
  const toggleColumn = (id: string) =>
    setHiddenColumns((prev) => {
      const next = new Set(prev);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      saveHiddenColumns(next);
      return next;
    });
  const resetColumns = () => {
    const defaults = new Set(DEFAULT_HIDDEN);
    saveHiddenColumns(defaults);
    setHiddenColumns(defaults);
  };
  const [searchError, setSearchError] = React.useState<string>();
  // Bumped by Search and by actions so data reloads even when the filter is unchanged.
  const [refresh, setRefresh] = React.useState(0);
  const ticket = React.useRef(0);

  const key = searchKey(applied);
  const windowFor = React.useCallback(
    (values: FailedFilterValues) =>
      selectedBucketWindow(values) ?? resolveWindow(values.period, new Date()),
    [],
  );

  // Chart: the whole range, without the selected bar.
  React.useEffect(() => {
    let cancelled = false;
    setHistogramLoading(true);
    const body = new api.FailedHistogramRequest();
    body.filter = toFailedSearchFilter(applied);
    body.period = periodOption(applied.period).value;
    client
      .postFailedHistogram(body)
      .then((h) => !cancelled && setHistogram(h))
      .catch(
        (e) =>
          !cancelled && console.error("Failed to load failure histogram", e),
      )
      .finally(() => !cancelled && setHistogramLoading(false));
    return () => {
      cancelled = true;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [key, refresh, client]);

  // All-time backlog across endpoints, for the "outside this range" hint.
  React.useEffect(() => {
    client
      .getEndpointStatusCountAll()
      .then((counts) => setBacklog(failureBacklog(counts)))
      .catch(() => setBacklog(undefined));
  }, [client, refresh]);

  const loadBlocked = React.useCallback(
    async (rows: api.Event[], forTicket: number) => {
      const byEndpoint = new Map<string, Set<string>>();
      for (const e of rows) {
        if (
          e.resolutionStatus !== api.ResolutionStatus.Failed ||
          !e.endpointId ||
          !e.sessionId
        )
          continue;
        if (!byEndpoint.has(e.endpointId))
          byEndpoint.set(e.endpointId, new Set());
        byEndpoint.get(e.endpointId)!.add(e.sessionId);
      }
      const results = await Promise.all(
        [...byEndpoint].map(async ([endpointId, sessions]) => {
          try {
            const statuses = await client.postEndpointSessionsBatch(
              endpointId,
              [...sessions],
            );
            const counts = deferredCountsBySession(
              statuses.flatMap((s) => s.deferredEvents ?? []),
            );
            return Object.entries(counts).map(
              ([session, n]) => [`${endpointId}/${session}`, n] as const,
            );
          } catch {
            return [];
          }
        }),
      );
      if (forTicket === ticket.current) {
        setBlocked((prev) => ({
          ...prev,
          ...Object.fromEntries(results.flat()),
        }));
      }
    },
    [client],
  );

  const fetchPage = React.useCallback(
    async (token: string | undefined, append: boolean) => {
      const current = append ? ticket.current : ++ticket.current;
      setListLoading(true);
      setSearchError(undefined);
      try {
        const body = new api.FailedSearchRequest();
        body.filter = toFailedSearchFilter(applied, windowFor(applied));
        body.maxSearchItemsCount = PAGE_SIZE;
        body.continuationToken = token;
        const response = await client.postFailedSearch(body);
        if (current !== ticket.current) return;
        const rows = response.events ?? [];
        setEvents((prev) => (append ? [...prev, ...rows] : rows));
        if (!append) setBlocked({});
        setContinuationToken(response.continuationToken ?? undefined);
        void loadBlocked(rows, current);
      } catch (e) {
        if (current === ticket.current) {
          setSearchError(e instanceof Error ? e.message : "The search failed.");
          if (!append) setEvents([]);
        }
      } finally {
        if (current === ticket.current) setListLoading(false);
      }
    },
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [key, applied.bucket, client, loadBlocked, windowFor],
  );

  // List: the range (or the selected bar).
  React.useEffect(() => {
    if (view !== "list") return;
    setContinuationToken(undefined);
    void fetchPage(undefined, false);
  }, [view, fetchPage, refresh]);

  // Error groups: every matching failure in the range (or the selected bar).
  React.useEffect(() => {
    if (view !== "error") return;
    let cancelled = false;
    setGroupsLoading(true);
    const body = new api.FailedErrorGroupsRequest();
    body.filter = toFailedSearchFilter(applied, windowFor(applied));
    client
      .postFailedErrorGroups(body)
      .then((g) => !cancelled && setErrorGroups(g))
      .catch((e) => !cancelled && console.error("Failed to group failures", e))
      .finally(() => !cancelled && setGroupsLoading(false));
    return () => {
      cancelled = true;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [view, key, applied.bucket, refresh, client, windowFor]);

  const act = React.useCallback(
    (
      action: "Resubmit" | "Skip",
      targets: { eventId?: string; lastMessageId?: string }[],
    ) => {
      const valid = targets.filter((t) => t.eventId && t.lastMessageId);
      if (valid.length === 0) return;
      const ids = new Set(valid.map((t) => t.eventId));
      setEvents((prev) => prev.filter((e) => !ids.has(e.eventId)));
      Promise.allSettled(
        valid.map((t) =>
          action === "Resubmit"
            ? client.postResubmitEventIds(t.eventId!, t.lastMessageId!)
            : client.postSkipEventIds(t.eventId!, t.lastMessageId!),
        ),
      ).then((results) => {
        const failed = results.filter((r) => r.status === "rejected").length;
        const done = results.length - failed;
        if (done > 0)
          notifySuccess(
            `${action === "Resubmit" ? "Resubmitted" : "Skipped"} ${done} failure${done === 1 ? "" : "s"}.`,
          );
        if (failed > 0)
          notifyError(
            `${failed} ${action.toLowerCase()} request${failed === 1 ? "" : "s"} failed.`,
          );
        setRefresh((n) => n + 1);
      });
    },
    [client],
  );

  const bodyActions = (e: api.Event): ITableBodyAction[] => [
    {
      name: "Resubmit",
      description: "Send the message to the endpoint again",
      onClick: () => {
        act("Resubmit", [e]);
        return false;
      },
    },
    {
      name: "Skip",
      description: "Mark the message as skipped and unblock its session",
      onClick: () => {
        act("Skip", [e]);
        return false;
      },
    },
  ];

  const headActions: ITableHeadAction[] = ["Resubmit", "Skip"].map((name) => ({
    name,
    onClick: (selected: ITableRow[]) => {
      const ids = new Set(selected.map((r) => r.id));
      act(
        name as "Resubmit" | "Skip",
        events.filter((e) => ids.has(`${e.endpointId}/${e.eventId}`)),
      );
      return false;
    },
  }));

  const narrow = (patch: Partial<FailedFilterValues>) =>
    applyFilters({ ...applied, ...patch });

  const visible = hideReported ? events.filter((e) => !e.isReported) : events;
  const rows: ITableRow[] = visible.map((e) => {
    const blockedCount = blocked[`${e.endpointId}/${e.sessionId}`] ?? 0;
    const error = errorTextOf(e);
    return {
      id: `${e.endpointId}/${e.eventId}`,
      route: `/Message/Index/${e.endpointId}/${e.eventId}/0`,
      bodyActions: bodyActions(e),
      tone: e.isReported ? "reported" : undefined,
      data: new Map([
        [
          "eventId",
          {
            value: (
              <TruncatedGuid
                guid={e.eventId}
                onClick={(g) => narrow({ eventId: g })}
              />
            ),
            searchValue: e.eventId ?? "",
          },
        ],
        [
          "status",
          {
            value: (
              <span className="inline-flex items-center gap-2">
                <Badge variant={statusVariant(e.resolutionStatus)} size="sm">
                  {e.resolutionStatus}
                </Badge>
                {blockedCount > 0 && (
                  <Badge
                    variant="warning"
                    size="sm"
                    withDot={false}
                    title={`${blockedCount} deferred message${blockedCount === 1 ? "" : "s"} waiting on this failed message in session ${e.sessionId}`}
                  >
                    ⏸ {blockedCount} blocked
                  </Badge>
                )}
              </span>
            ),
            searchValue: e.resolutionStatus ?? "",
          },
        ],
        [
          "endpoint",
          {
            value: (
              <Link
                to={`/Endpoints/Details/${e.endpointId}`}
                onClick={(ev) => ev.stopPropagation()}
                className="font-semibold hover:underline"
              >
                {e.endpointId}
              </Link>
            ),
            searchValue: e.endpointId ?? "",
          },
        ],
        [
          "sessionId",
          {
            value: (
              <TruncatedGuid
                guid={e.sessionId}
                onClick={(g) => narrow({ sessionId: g })}
              />
            ),
            searchValue: e.sessionId ?? "",
          },
        ],
        [
          "error",
          {
            value: (
              <span className="block min-w-0" title={error}>
                <span className="block truncate">{e.eventTypeId}</span>
                <span className="block truncate font-mono text-[11.5px] text-status-danger-ink">
                  {error ?? "—"}
                </span>
              </span>
            ),
            searchValue: `${e.eventTypeId ?? ""} ${error ?? ""}`,
          },
        ],
        [
          "resubmitCount",
          {
            value:
              (e.resubmitCount ?? 0) > 0 ? (
                <Badge variant="info" size="sm">
                  {e.resubmitCount}
                </Badge>
              ) : null,
            searchValue: String(e.resubmitCount ?? 0),
          },
        ],
        [
          "reported",
          {
            value: e.isReported ? (
              <Badge
                variant="info"
                size="sm"
                withDot={false}
                title={e.reportedBy ? `Reported by ${e.reportedBy}` : undefined}
              >
                {e.ticketId || "Reported"}
              </Badge>
            ) : null,
            searchValue: e.isReported ? (e.ticketId ?? "reported") : "",
          },
        ],
        [
          "updated",
          {
            value: formatMoment(e.updatedAt, true),
            searchValue: formatMoment(e.updatedAt) || "",
          },
        ],
        [
          "added",
          {
            value: formatMoment(e.enqueuedTimeUtc, true),
            searchValue: formatMoment(e.enqueuedTimeUtc) || "",
          },
        ],
      ]),
    };
  });

  const totals = histogram?.totals;
  const inRange =
    (totals?.failed ?? 0) +
    (totals?.deadLettered ?? 0) +
    (totals?.unsupported ?? 0);
  const option = periodOption(applied.period);
  const selectedWindow = selectedBucketWindow(applied);
  const loadingTile = histogramLoading && !histogram;
  const peak = (histogram?.buckets ?? []).reduce<
    api.FailedHistogramBucket | undefined
  >((best, b) => {
    const total =
      (b.failed ?? 0) + (b.deadLettered ?? 0) + (b.unsupported ?? 0);
    const bestTotal = best
      ? (best.failed ?? 0) + (best.deadLettered ?? 0) + (best.unsupported ?? 0)
      : 0;
    return total > bestTotal ? b : best;
  }, undefined);
  // The backlog counts every failure on every endpoint, so the hint is only honest unfiltered.
  const unfiltered = SEARCH_FIELDS.every((f) =>
    Array.isArray(applied[f]) ? applied[f].length === 0 : !applied[f],
  );
  const outsideRange =
    backlog !== undefined && unfiltered ? Math.max(0, backlog - inRange) : 0;

  const segBtn = (active: boolean) =>
    cn(
      "px-3 py-1.5 rounded-md text-xs font-semibold transition-colors",
      active
        ? "bg-primary text-white"
        : "text-muted-foreground hover:text-foreground",
    );

  return (
    <Page
      title="Failed messages"
      subtitle="Unresolved failures across every endpoint you can read"
    >
      <div className="flex w-full flex-col gap-4">
        <div className="flex flex-wrap items-center gap-2">
          <div
            className="inline-flex items-center gap-[2px] rounded-nb-md border border-border bg-card p-[3px]"
            role="group"
            aria-label="Time range"
          >
            {PERIOD_OPTIONS.map((p) => (
              <button
                key={p.value}
                type="button"
                className={segBtn(option.value === p.value)}
                aria-pressed={option.value === p.value}
                onClick={() => narrow({ period: p.value, bucket: "" })}
              >
                {p.label}
              </button>
            ))}
          </div>
          {outsideRange > 0 && (
            <span className="text-[13px] text-muted-foreground">
              {outsideRange.toLocaleString()} older unresolved failure
              {outsideRange === 1 ? "" : "s"} outside this range
              {option.value !== api.Period._30d && (
                <>
                  {" — "}
                  <button
                    type="button"
                    className="font-semibold text-primary-700 hover:underline"
                    onClick={() =>
                      narrow({ period: api.Period._30d, bucket: "" })
                    }
                  >
                    show 30d
                  </button>
                </>
              )}
            </span>
          )}
        </div>

        <StatRow columns={5}>
          <StatTile
            label="Total"
            value={loadingTile ? "—" : inRange.toLocaleString()}
            tone={inRange > 0 ? "danger" : "muted"}
            delta={`last ${option.label}`}
          />
          <StatTile
            label="Failed"
            value={loadingTile ? "—" : (totals?.failed ?? 0).toLocaleString()}
            tone={(totals?.failed ?? 0) > 0 ? "danger" : "muted"}
            delta="handler threw"
          />
          <StatTile
            label="DeadLettered"
            value={
              loadingTile ? "—" : (totals?.deadLettered ?? 0).toLocaleString()
            }
            tone={(totals?.deadLettered ?? 0) > 0 ? "danger" : "muted"}
            delta="max deliveries hit"
          />
          <StatTile
            label="Unsupported"
            value={
              loadingTile ? "—" : (totals?.unsupported ?? 0).toLocaleString()
            }
            tone="muted"
            delta="no handler"
          />
          <StatTile
            label="Endpoints affected"
            value={
              loadingTile
                ? "—"
                : (totals?.byEndpoint?.length ?? 0).toLocaleString()
            }
            tone="muted"
            delta={
              peak
                ? `peak ${formatBucket(peak.start, histogram?.bucketMinutes ?? 60, true)}`
                : "no failures"
            }
          />
        </StatRow>

        <section
          aria-label="Failures over time"
          className="rounded-lg border border-border bg-card p-4"
        >
          <div className="mb-2 flex flex-wrap items-center justify-between gap-2">
            <div className="flex items-baseline gap-3">
              <h2 className="m-0 text-[15px] font-bold">Failures over time</h2>
              <span className="font-mono text-[12px] text-muted-foreground">
                {option.bucketMinutes >= 1440
                  ? "daily"
                  : option.bucketMinutes >= 60
                    ? `${option.bucketMinutes / 60}-hour`
                    : `${option.bucketMinutes}-minute`}{" "}
                buckets · by time of last failure
              </span>
            </div>
            <div
              className="inline-flex items-center gap-[2px] rounded-nb-md border border-border bg-card p-[3px]"
              role="group"
              aria-label="Chart split"
            >
              <button
                type="button"
                className={segBtn(split === "status")}
                onClick={() => narrow({ split: "status" })}
              >
                By status
              </button>
              <button
                type="button"
                className={segBtn(split === "endpoint")}
                onClick={() => narrow({ split: "endpoint" })}
              >
                By endpoint
              </button>
            </div>
          </div>
          <FailedHistogram
            histogram={histogram}
            split={split}
            selectedBucket={applied.bucket}
            onSelectBucket={(bucket) => narrow({ bucket })}
            isLoading={histogramLoading}
          />
          <div className="mt-2 flex flex-wrap items-center justify-between gap-2 text-[12.5px] text-muted-foreground">
            <span>Click a bar to narrow the list to that window.</span>
            {selectedWindow && (
              <span className="inline-flex items-center gap-1 rounded-full bg-primary-50 px-3 py-1 font-semibold text-primary-700">
                Window{" "}
                {formatBucket(selectedWindow.from, option.bucketMinutes, true)}
                <button
                  type="button"
                  aria-label="Clear time window"
                  className="px-1 text-[15px]"
                  onClick={() => narrow({ bucket: "" })}
                >
                  ×
                </button>
              </span>
            )}
          </div>
          {histogram?.truncated && (
            <p className="mt-1 text-[12.5px] text-status-warning-ink">
              The chart stopped counting at the store&apos;s cap; counts are a
              lower bound.
            </p>
          )}
        </section>

        <FailedFilterBar
          value={applied}
          statusCounts={
            totals
              ? {
                  Failed: totals.failed,
                  DeadLettered: totals.deadLettered,
                  Unsupported: totals.unsupported,
                }
              : undefined
          }
          onSearch={(next) => {
            applyFilters({ ...next, bucket: applied.bucket });
            setRefresh((n) => n + 1);
          }}
          onReset={resetFilters}
          isLoading={listLoading || groupsLoading}
        />

        <div className="flex flex-wrap items-center gap-2 text-sm">
          <span className="text-[13px] font-semibold text-muted-foreground">
            View:
          </span>
          <div
            className="inline-flex items-center gap-[2px] rounded-nb-md border border-border bg-card p-[3px]"
            role="group"
            aria-label="View"
          >
            <button
              type="button"
              className={segBtn(view === "list")}
              onClick={() => narrow({ view: "list" })}
            >
              List
            </button>
            <button
              type="button"
              className={segBtn(view === "endpoint")}
              onClick={() => narrow({ view: "endpoint" })}
            >
              By endpoint
            </button>
            <button
              type="button"
              className={segBtn(view === "error")}
              onClick={() => narrow({ view: "error" })}
            >
              By error
            </button>
          </div>
          {view === "list" && (
            <div className="ml-auto flex items-center gap-3">
              <label className="inline-flex cursor-pointer select-none items-center gap-2 text-muted-foreground">
                <Checkbox
                  checked={hideReported}
                  onChange={(e) => setHideReported(e.target.checked)}
                  aria-label="Hide reported failures"
                />
                Hide reported
              </label>
              <ColumnChooser
                columns={COLUMNS}
                hidden={hiddenColumns}
                onToggle={toggleColumn}
                onReset={resetColumns}
              />
            </div>
          )}
        </div>

        {searchError ? (
          <div
            role="alert"
            className="rounded-nb-md border border-border bg-card p-6"
          >
            <p className="font-semibold">Search failed</p>
            <p className="text-sm text-muted-foreground">{searchError}</p>
          </div>
        ) : view === "error" ? (
          <FailedErrorGroupsView
            groups={errorGroups}
            isLoading={groupsLoading}
            onAct={act}
          />
        ) : view === "endpoint" ? (
          <EndpointSummary
            totals={totals?.byEndpoint ?? []}
            onShow={(endpointId) =>
              narrow({ endpointId: [endpointId], view: "list" })
            }
          />
        ) : !listLoading && rows.length === 0 ? (
          <EmptyState
            icon="◌"
            title="No failures match"
            description="No unresolved failures match these filters in this window. Try a wider range or fewer filters."
          />
        ) : (
          <DataTable
            headCells={headCells}
            headActions={headActions}
            rows={rows}
            withCheckboxes={true}
            rowActions="menu"
            noDataMessage="No failures"
            isLoading={listLoading}
            count={rows.length}
            hideDense={true}
            dataRowsPerPage={20}
            onPageChange={() => {
              if (continuationToken && !listLoading)
                void fetchPage(continuationToken, true);
            }}
            hasMoreRows={!!continuationToken}
            fixedWidth={"-webkit-fill-available"}
          />
        )}
      </div>
    </Page>
  );
}

function EndpointSummary({
  totals,
  onShow,
}: {
  totals: api.FailedEndpointTotals[];
  onShow: (endpointId: string) => void;
}) {
  if (totals.length === 0) {
    return (
      <EmptyState
        icon="◌"
        title="No failures in this window"
        description="No endpoint has unresolved failures matching these filters."
      />
    );
  }
  const cell = "px-3 py-2.5";
  return (
    <table className="w-full border-collapse text-sm">
      <thead>
        <tr className="border-b border-border bg-muted text-left font-mono text-[11px] uppercase tracking-wide text-muted-foreground">
          <th className={cn(cell, "font-medium")}>Endpoint</th>
          <th className={cn(cell, "font-medium text-right")}>Failed</th>
          <th className={cn(cell, "font-medium text-right")}>DeadLettered</th>
          <th className={cn(cell, "font-medium text-right")}>Unsupported</th>
          <th className={cn(cell, "font-medium text-right")}>Total</th>
          <th className={cell} />
        </tr>
      </thead>
      <tbody>
        {totals.map((t) => (
          <tr key={t.endpointId} className="border-b border-border">
            <td className={cell}>
              <Link
                to={`/Endpoints/Details/${t.endpointId}?status=Failed&status=DeadLettered&status=Unsupported`}
                className="font-semibold hover:underline"
              >
                {t.endpointId}
              </Link>
            </td>
            <td className={cn(cell, "text-right font-mono")}>{t.failed}</td>
            <td className={cn(cell, "text-right font-mono")}>
              {t.deadLettered}
            </td>
            <td className={cn(cell, "text-right font-mono")}>
              {t.unsupported}
            </td>
            <td className={cn(cell, "text-right font-mono font-semibold")}>
              {(t.failed ?? 0) + (t.deadLettered ?? 0) + (t.unsupported ?? 0)}
            </td>
            <td className={cn(cell, "text-right")}>
              <button
                type="button"
                onClick={() => onShow(t.endpointId!)}
                className="rounded-md border border-border-strong px-3 py-1 text-[12px] font-semibold hover:bg-accent"
              >
                Show failures
              </button>
            </td>
          </tr>
        ))}
      </tbody>
    </table>
  );
}
