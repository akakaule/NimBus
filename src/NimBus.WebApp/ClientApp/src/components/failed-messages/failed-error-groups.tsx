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
  const shown = list.slice(0, 3);
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
          title={list.slice(3).join(", ")}
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
    <div className="flex justify-end gap-1.5">
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
  const eventRows = (events: api.FailedEventRef[], indent: string) =>
    events.map((ev) => (
      <tr
        key={`${ev.endpointId}/${ev.eventId}`}
        className="group border-b border-border bg-muted/30"
      >
        <td className={cn(cell, indent)}>
          <Link
            to={`/Message/Index/${ev.endpointId}/${ev.eventId}/0`}
            className="font-mono text-[12px] text-primary-700 hover:underline"
          >
            {(ev.eventId ?? "").slice(0, 8)}…
          </Link>
        </td>
        <td className={cell}>
          <Badge variant={statusVariant(ev.resolutionStatus)} size="sm">
            {ev.resolutionStatus}
          </Badge>
        </td>
        <td className={cell}>
          <Link
            to={`/Endpoints/Details/${ev.endpointId}`}
            className="text-[13px] font-semibold hover:underline"
          >
            {ev.endpointId}
          </Link>
          <div className="text-[11.5px] text-muted-foreground">
            {ev.eventTypeId}
          </div>
        </td>
        <td className={cell}>
          <TruncatedGuid guid={ev.sessionId} />
        </td>
        <td className={cn(cell, "whitespace-nowrap font-mono text-[12px]")}>
          {formatMoment(ev.updatedAt, true)}
        </td>
        <td
          className={cn(
            cell,
            "max-w-md truncate font-mono text-[12px] text-status-danger-ink",
          )}
          title={ev.errorText ?? ""}
        >
          {ev.errorText}
        </td>
        <td className={cell}>
          <GroupActions events={[ev]} onAct={onAct} />
        </td>
      </tr>
    ));

  return (
    <div className="overflow-x-auto">
      {groups?.truncated && (
        <p className="mb-2 text-[13px] text-status-warning-ink">
          Grouped the newest {groups.total?.toLocaleString()} failures only —
          narrow the filters to group the rest.
        </p>
      )}
      <table className="w-full border-collapse text-sm">
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
                  <td className={cn(cell, "max-w-xs")}>
                    <span className="inline-flex items-start gap-1 font-mono text-[12px]">
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
                      "max-w-sm truncate text-[12px] text-muted-foreground",
                    )}
                    title={group.exampleErrorText}
                  >
                    {group.exampleErrorText}
                  </td>
                  <td className={cell}>
                    <GroupActions events={allEvents(group)} onAct={onAct} />
                  </td>
                </tr>
                {groupOpen && single && eventRows(subs[0].events ?? [], "pl-8")}
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
                          <td className={cn(cell, "max-w-xs pl-8")}>
                            <span className="inline-flex items-start gap-1 font-mono text-[12px] text-muted-foreground">
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
                              "max-w-sm truncate text-[12px] text-muted-foreground",
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
                        {patternOpen && eventRows(sub.events ?? [], "pl-14")}
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
