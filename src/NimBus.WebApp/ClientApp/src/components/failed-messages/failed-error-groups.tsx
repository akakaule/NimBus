import * as React from "react";
import * as api from "api-client";
import { Link } from "react-router-dom";
import { Badge } from "components/ui/badge";
import { EmptyState } from "components/ui/empty-state";
import TruncatedGuid from "components/common/truncated-guid";
import { formatMoment } from "functions/endpoint.functions";
import { cn } from "lib/utils";

export interface FailedErrorGroupsProps {
  groups: api.FailedErrorGroups | undefined;
  isLoading: boolean;
  /** Resubmits or skips the given failures; resolves once the requests were sent. */
  onAct: (action: "Resubmit" | "Skip", events: api.FailedEventRef[]) => void;
}

const statusVariant = (status: string | undefined) =>
  status === "DeadLettered"
    ? "deadlettered"
    : status === "Unsupported"
      ? "unsupported"
      : "failed";

const chevron = (open: boolean) => (
  <span
    aria-hidden
    className="inline-block w-3 text-[10px] text-muted-foreground"
  >
    {open ? "▼" : "▶"}
  </span>
);

function Chips({
  values,
  variant,
}: {
  values: string[] | undefined;
  variant: "default" | "info";
}) {
  const list = values ?? [];
  const shown = list.slice(0, 2);
  return (
    <div className="flex flex-wrap gap-1">
      {shown.map((v) => (
        <Badge key={v} variant={variant} size="sm" withDot={variant === "info"}>
          {v}
        </Badge>
      ))}
      {list.length > shown.length && (
        <span
          className="text-[11px] text-muted-foreground"
          title={list.slice(2).join(", ")}
        >
          +{list.length - shown.length}
        </span>
      )}
    </div>
  );
}

function GroupActions({
  events,
  onAct,
}: {
  events: api.FailedEventRef[];
  onAct: FailedErrorGroupsProps["onAct"];
}) {
  const act = (action: "Resubmit" | "Skip") => (e: React.MouseEvent) => {
    e.stopPropagation();
    const noun = events.length === 1 ? "failure" : "failures";
    if (
      events.length > 1 &&
      !window.confirm(`${action} ${events.length} ${noun}?`)
    )
      return;
    onAct(action, events);
  };
  return (
    <div className="flex justify-end gap-1.5 whitespace-nowrap">
      <button
        type="button"
        onClick={act("Resubmit")}
        className="rounded-md border border-primary-100 bg-primary-50 px-2 py-1 text-[12px] font-semibold text-primary-700 hover:bg-primary-100"
      >
        Resubmit{events.length > 1 ? ` ${events.length}` : ""}
      </button>
      <button
        type="button"
        onClick={act("Skip")}
        className="rounded-md border border-border px-2 py-1 text-[12px] font-semibold text-muted-foreground hover:text-foreground"
      >
        Skip
      </button>
    </div>
  );
}

// Failures listed per expanded pattern before "Show more".
const FAILURES_PER_PAGE = 20;

const allEvents = (group: api.FailedErrorGroup): api.FailedEventRef[] =>
  (group.subGroups ?? []).flatMap((s) => s.events ?? []);

/**
 * Failures grouped like the Insights page: error category, then normalized pattern, then the
 * individual failures. Every level can be expanded and resubmitted or skipped as a whole.
 */
export default function FailedErrorGroupsView({
  groups,
  isLoading,
  onAct,
}: FailedErrorGroupsProps) {
  const [openGroups, setOpenGroups] = React.useState<Set<string>>(new Set());
  const [openPatterns, setOpenPatterns] = React.useState<Set<string>>(
    new Set(),
  );
  const [shownCounts, setShownCounts] = React.useState<Record<string, number>>(
    {},
  );

  const toggle = (
    setter: React.Dispatch<React.SetStateAction<Set<string>>>,
    key: string,
  ) =>
    setter((prev) => {
      const next = new Set(prev);
      if (next.has(key)) next.delete(key);
      else next.add(key);
      return next;
    });

  if (isLoading && !groups) {
    return (
      <div
        className="h-40 animate-pulse rounded-md bg-muted"
        aria-label="Loading error groups"
      />
    );
  }

  const list = groups?.groups ?? [];
  if (list.length === 0) {
    return (
      <EmptyState
        icon="◌"
        title="No failures to group"
        description="No unresolved failures match these filters in this window."
      />
    );
  }

  const cell = "px-3 py-2 align-top";
  const eventRow = (ev: api.FailedEventRef, indent: string) => (
    <tr
      key={`${ev.endpointId}/${ev.eventId}`}
      className="group border-b border-border bg-muted/30"
    >
      {/* Cells line up with the group columns: failure | – | endpoint | event type | updated | error */}
      <td className={cn(cell, indent)}>
        <span className="flex min-w-0 items-center gap-2">
          <Link
            to={`/Message/Index/${ev.endpointId}/${ev.eventId}/0`}
            className="font-mono text-[12px] text-primary-700 hover:underline"
          >
            {(ev.eventId ?? "").slice(0, 8)}…
          </Link>
          <Badge variant={statusVariant(ev.resolutionStatus)} size="sm">
            {ev.resolutionStatus}
          </Badge>
        </span>
      </td>
      <td className={cell} />
      <td className={cn(cell, "truncate")}>
        <Link
          to={`/Endpoints/Details/${ev.endpointId}`}
          className="text-[13px] font-semibold hover:underline"
        >
          {ev.endpointId}
        </Link>
      </td>
      <td className={cell}>
        <div className="truncate text-[12px]">{ev.eventTypeId}</div>
        <TruncatedGuid guid={ev.sessionId} />
      </td>
      <td className={cn(cell, "whitespace-nowrap font-mono text-[12px]")}>
        {formatMoment(ev.updatedAt, true)}
      </td>
      <td
        className={cn(
          cell,
          "truncate font-mono text-[12px] text-status-danger-ink",
        )}
        title={ev.errorText ?? ""}
      >
        {ev.errorText}
      </td>
      <td className={cell}>
        <GroupActions events={[ev]} onAct={onAct} />
      </td>
    </tr>
  );

  // A pattern can hold hundreds of failures; show a page at a time.
  const eventRows = (
    events: api.FailedEventRef[],
    indent: string,
    key: string,
  ) => {
    const limit = shownCounts[key] ?? FAILURES_PER_PAGE;
    const rest = events.length - limit;
    return [
      ...events.slice(0, limit).map((ev) => eventRow(ev, indent)),
      rest > 0 && (
        <tr key={`${key}/more`} className="border-b border-border bg-muted/30">
          <td colSpan={7} className={cn(cell, indent)}>
            <button
              type="button"
              className="text-[12.5px] font-semibold text-primary-700 hover:underline"
              onClick={() =>
                setShownCounts((prev) => ({
                  ...prev,
                  [key]: limit + FAILURES_PER_PAGE,
                }))
              }
            >
              Show {Math.min(rest, FAILURES_PER_PAGE)} more
              {rest > FAILURES_PER_PAGE && ` (${rest} remaining)`}
            </button>
          </td>
        </tr>
      ),
    ];
  };

  return (
    <div className="overflow-x-auto">
      {groups?.truncated && (
        <p className="mb-2 text-[13px] text-status-warning-ink">
          Grouped the newest {groups.total?.toLocaleString()} failures only —
          narrow the filters to group the rest.
        </p>
      )}
      <table className="w-full table-fixed border-collapse text-sm">
        <colgroup>
          <col className="w-[24%]" />
          <col className="w-[72px]" />
          <col className="w-[14%]" />
          <col className="w-[16%]" />
          <col className="w-[128px]" />
          <col />
          <col className="w-[172px]" />
        </colgroup>
        <thead>
          <tr className="border-b border-border bg-muted text-left font-mono text-[11px] uppercase tracking-wide text-muted-foreground">
            <th className="px-3 py-2 font-medium">Error</th>
            <th className="px-3 py-2 font-medium">Count</th>
            <th className="px-3 py-2 font-medium">Endpoints</th>
            <th className="px-3 py-2 font-medium">Event types</th>
            <th className="px-3 py-2 font-medium">Latest</th>
            <th className="px-3 py-2 font-medium">Example error</th>
            <th className="px-3 py-2 font-medium" />
          </tr>
        </thead>
        <tbody>
          {list.map((group) => {
            const groupKey = group.errorCategory ?? "";
            const groupOpen = openGroups.has(groupKey);
            const subs = group.subGroups ?? [];
            // A category with one pattern expands straight to its failures.
            const single = subs.length === 1;
            return (
              <React.Fragment key={groupKey}>
                <tr
                  className="cursor-pointer border-b border-border hover:bg-accent"
                  onClick={() => toggle(setOpenGroups, groupKey)}
                  aria-expanded={groupOpen}
                >
                  <td className={cell}>
                    <span className="flex min-w-0 items-start gap-1 font-mono text-[12px]">
                      {chevron(groupOpen)}
                      <span className="truncate" title={groupKey}>
                        {groupKey}
                      </span>
                    </span>
                    {!single && (
                      <div className="ml-4 text-[11px] text-muted-foreground">
                        {subs.length} patterns
                      </div>
                    )}
                  </td>
                  <td className={cell}>
                    <Badge variant="failed" size="sm">
                      {group.count}
                    </Badge>
                  </td>
                  <td className={cell}>
                    <Chips values={group.endpoints} variant="default" />
                  </td>
                  <td className={cell}>
                    <Chips values={group.eventTypes} variant="info" />
                  </td>
                  <td
                    className={cn(
                      cell,
                      "whitespace-nowrap font-mono text-[12px]",
                    )}
                  >
                    {formatMoment(group.latestOccurrence, true)}
                  </td>
                  <td
                    className={cn(
                      cell,
                      "truncate text-[12px] text-muted-foreground",
                    )}
                    title={group.exampleErrorText}
                  >
                    {group.exampleErrorText}
                  </td>
                  <td className={cell}>
                    <GroupActions events={allEvents(group)} onAct={onAct} />
                  </td>
                </tr>
                {groupOpen &&
                  single &&
                  eventRows(subs[0].events ?? [], "pl-8", groupKey)}
                {groupOpen &&
                  !single &&
                  subs.map((sub) => {
                    const patternKey = `${groupKey}\u0000${sub.normalizedPattern}`;
                    const patternOpen = openPatterns.has(patternKey);
                    return (
                      <React.Fragment key={patternKey}>
                        <tr
                          className="cursor-pointer border-b border-border bg-muted/50 hover:bg-accent"
                          onClick={() => toggle(setOpenPatterns, patternKey)}
                          aria-expanded={patternOpen}
                        >
                          <td className={cn(cell, "pl-8")}>
                            <span className="flex min-w-0 items-start gap-1 font-mono text-[12px] text-muted-foreground">
                              {chevron(patternOpen)}
                              <span
                                className="truncate"
                                title={sub.normalizedPattern}
                              >
                                {sub.normalizedPattern}
                              </span>
                            </span>
                          </td>
                          <td className={cell}>
                            <Badge variant="failed" size="sm">
                              {sub.count}
                            </Badge>
                          </td>
                          <td className={cell}>
                            <Chips values={sub.endpoints} variant="default" />
                          </td>
                          <td className={cell}>
                            <Chips values={sub.eventTypes} variant="info" />
                          </td>
                          <td
                            className={cn(
                              cell,
                              "whitespace-nowrap font-mono text-[12px]",
                            )}
                          >
                            {formatMoment(sub.latestOccurrence, true)}
                          </td>
                          <td
                            className={cn(
                              cell,
                              "truncate text-[12px] text-muted-foreground",
                            )}
                            title={sub.exampleErrorText}
                          >
                            {sub.exampleErrorText}
                          </td>
                          <td className={cell}>
                            <GroupActions
                              events={sub.events ?? []}
                              onAct={onAct}
                            />
                          </td>
                        </tr>
                        {patternOpen &&
                          eventRows(sub.events ?? [], "pl-14", patternKey)}
                      </React.Fragment>
                    );
                  })}
              </React.Fragment>
            );
          })}
        </tbody>
      </table>
    </div>
  );
}
