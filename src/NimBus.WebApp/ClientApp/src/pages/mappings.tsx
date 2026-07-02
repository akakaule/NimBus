import { useState, useEffect, useCallback, useMemo } from "react";
import * as api from "api-client";
import Page from "components/page";
import Loading from "components/loading/loading";
import { Badge, type BadgeVariant } from "components/ui/badge";
import { Button } from "components/ui/button";
import {
  Card,
  CardHeader,
  CardTitle,
  CardContent,
} from "components/ui/card";
import { CodeBlock } from "components/ui/code-block";
import { EmptyState } from "components/ui/empty-state";
import { useToast } from "components/ui/toast";

// ── helpers ──────────────────────────────────────────────────────────────────

function stateBadgeVariant(
  state: api.MappingInfoState | undefined,
): BadgeVariant {
  switch (state) {
    case api.MappingInfoState.Draft:
      return "info";
    case api.MappingInfoState.Active:
      return "success";
    case api.MappingInfoState.Paused:
      return "warning";
    case api.MappingInfoState.Stale:
      return "warning";
    case api.MappingInfoState.Rejected:
      return "error";
    default:
      return "default";
  }
}

interface WorkedExample {
  source: unknown;
  output: unknown;
}

function parseWorkedExamples(json: string | undefined): WorkedExample[] {
  if (!json) return [];
  try {
    const parsed = JSON.parse(json);
    if (!Array.isArray(parsed)) return [];
    return parsed.filter(
      (item) =>
        item !== null &&
        typeof item === "object" &&
        "source" in item &&
        "output" in item,
    ) as WorkedExample[];
  } catch {
    return [];
  }
}

/**
 * Maps an error thrown by a mapping-lifecycle action to a user-facing toast
 * description. The NSwag-generated {@link api.SwaggerException} extends `Error`
 * but carries the numeric HTTP `status`, so it must be detected *before* the
 * generic `Error` branch — otherwise a real 409 is read as a message string and
 * the schema-drift guidance is never shown.
 */
export function describeActionError(err: unknown): string {
  if (
    err !== null &&
    typeof err === "object" &&
    api.SwaggerException.isSwaggerException(err)
  ) {
    if (err.status === 409) {
      return "Mapping has drifted since proposal — reject and re-propose.";
    }
    return `Action failed (${err.status}).`;
  }
  if (err instanceof Error) {
    return `Action failed (${err.message}).`;
  }
  return "Action failed (unknown).";
}

// ── sub-components ───────────────────────────────────────────────────────────

function MappingDetailPanel({
  mapping,
  onRefresh,
}: {
  mapping: api.MappingInfo;
  onRefresh: () => void;
}) {
  const { addToast } = useToast();
  const [acting, setActing] = useState(false);

  const runAction = useCallback(
    async (fn: () => Promise<void>, successMsg: string) => {
      setActing(true);
      try {
        await fn();
        addToast({ variant: "success", title: successMsg });
        onRefresh();
      } catch (err: unknown) {
        addToast({
          variant: "error",
          title: "Action failed",
          description: describeActionError(err),
        });
      } finally {
        setActing(false);
      }
    },
    [addToast, onRefresh],
  );

  const client = new api.Client(api.CookieAuth());

  const handleApprove = () =>
    runAction(
      () => client.postAgentMappingApprove(mapping.id!),
      "Mapping approved.",
    );
  const handleReject = () =>
    runAction(
      () => client.postAgentMappingReject(mapping.id!),
      "Mapping rejected.",
    );
  const handlePause = () =>
    runAction(
      () => client.postAgentMappingPause(mapping.id!),
      "Mapping paused.",
    );
  const handleResume = () =>
    runAction(
      () => client.postAgentMappingResume(mapping.id!),
      "Mapping resumed.",
    );

  const examples = parseWorkedExamples(mapping.workedExamplesJson);

  return (
    <div className="flex flex-col gap-4 min-w-0">
      {/* Header row */}
      <div className="flex items-center justify-between gap-4 flex-wrap">
        <div className="flex items-center gap-2 flex-wrap">
          <span className="font-semibold text-sm">
            {mapping.sourceEventTypeId}
          </span>
          <span className="text-muted-foreground text-sm">→</span>
          <span className="font-semibold text-sm">
            {mapping.targetEventTypeId}
          </span>
          <Badge variant={stateBadgeVariant(mapping.state)} size="sm">
            {mapping.state ?? "Unknown"}
          </Badge>
          {mapping.state === api.MappingInfoState.Stale && (
            <Badge variant="warning" size="sm">
              Schema drift detected
            </Badge>
          )}
          {mapping.version !== undefined && (
            <span className="text-xs text-muted-foreground font-mono">
              v{mapping.version}
            </span>
          )}
        </div>

        {/* Action buttons */}
        <div className="flex items-center gap-2 flex-wrap">
          {mapping.state === api.MappingInfoState.Draft && (
            <>
              <Button
                variant="solid"
                colorScheme="green"
                size="sm"
                isLoading={acting}
                onClick={handleApprove}
                aria-label="Approve mapping"
              >
                Approve
              </Button>
              <Button
                variant="outline"
                colorScheme="red"
                size="sm"
                isLoading={acting}
                onClick={handleReject}
                aria-label="Reject mapping"
              >
                Reject
              </Button>
            </>
          )}
          {mapping.state === api.MappingInfoState.Active && (
            <Button
              variant="outline"
              colorScheme="gray"
              size="sm"
              isLoading={acting}
              onClick={handlePause}
              aria-label="Pause mapping"
            >
              Pause
            </Button>
          )}
          {mapping.state === api.MappingInfoState.Paused && (
            <Button
              variant="solid"
              colorScheme="primary"
              size="sm"
              isLoading={acting}
              onClick={handleResume}
              aria-label="Resume mapping"
            >
              Resume
            </Button>
          )}
        </div>
      </div>

      {/* Rationale */}
      {mapping.rationale && (
        <Card>
          <CardHeader>
            <CardTitle className="text-sm">Rationale</CardTitle>
          </CardHeader>
          <CardContent>
            <p className="text-sm text-muted-foreground leading-relaxed">
              {mapping.rationale}
            </p>
          </CardContent>
        </Card>
      )}

      {/* Transform */}
      {mapping.transform && (
        <CodeBlock title="Transform" highlight="none">
          {mapping.transform}
        </CodeBlock>
      )}

      {/* Worked examples */}
      {examples.length > 0 && (
        <Card>
          <CardHeader>
            <CardTitle className="text-sm">
              Worked Examples ({examples.length})
            </CardTitle>
          </CardHeader>
          <CardContent className="flex flex-col gap-4">
            {examples.map((ex, i) => (
              <div key={i} className="flex flex-col gap-2">
                <span className="text-xs font-mono text-muted-foreground uppercase tracking-wider">
                  Example {i + 1}
                </span>
                <div className="grid grid-cols-2 gap-3">
                  <CodeBlock title="Source" highlight="json">
                    {JSON.stringify(ex.source, null, 2)}
                  </CodeBlock>
                  <CodeBlock title="Output" highlight="json">
                    {JSON.stringify(ex.output, null, 2)}
                  </CodeBlock>
                </div>
              </div>
            ))}
          </CardContent>
        </Card>
      )}
    </div>
  );
}

// ── main page ─────────────────────────────────────────────────────────────────

export default function MappingsPage() {
  const [mappings, setMappings] = useState<api.MappingInfo[]>([]);
  const [loading, setLoading] = useState(true);
  // Key the selection by id (not the object) so a refresh re-resolves the
  // selected mapping from the freshly-fetched list — keeping it selected and
  // showing its updated state after an Approve/Reject/Pause/Resume action.
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const selected = useMemo(
    () => mappings.find((m) => m.id === selectedId) ?? null,
    [mappings, selectedId],
  );

  const fetchMappings = useCallback(async () => {
    setLoading(true);
    try {
      const client = new api.Client(api.CookieAuth());
      const result = await client.getAgentMappings();
      setMappings(result ?? []);
    } catch (err) {
      console.error("Failed to fetch mappings", err);
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    fetchMappings();
  }, [fetchMappings]);

  const handleRefresh = useCallback(() => {
    fetchMappings();
  }, [fetchMappings]);

  if (loading && mappings.length === 0) {
    return (
      <div className="flex flex-1 justify-center items-center">
        <Loading />
      </div>
    );
  }

  return (
    <Page title="Mappings">
      <div className="flex flex-col w-full gap-6">
        {/* Mapping list */}
        <Card>
          <CardHeader>
            <CardTitle className="text-sm">All Mappings</CardTitle>
          </CardHeader>
          {mappings.length === 0 ? (
            <CardContent>
              <EmptyState
                title="No mappings yet"
                description="AI-proposed mappings will appear here for review."
              />
            </CardContent>
          ) : (
            <div className="divide-y divide-border">
              {mappings.map((m) => (
                <div
                  key={m.id}
                  data-testid="mapping-row"
                  className={
                    "flex items-center gap-3 px-4 py-3 cursor-pointer transition-colors " +
                    (selectedId === m.id
                      ? "bg-primary/[0.08]"
                      : "hover:bg-muted")
                  }
                  onClick={() =>
                    setSelectedId((prev) => (prev === m.id ? null : m.id ?? null))
                  }
                >
                  <span className="font-medium text-sm truncate min-w-0">
                    {m.sourceEventTypeId ?? "—"}
                  </span>
                  <span className="text-muted-foreground text-sm shrink-0">
                    →
                  </span>
                  <span className="font-medium text-sm truncate flex-1 min-w-0">
                    {m.targetEventTypeId ?? "—"}
                  </span>
                  <Badge variant={stateBadgeVariant(m.state)} size="sm">
                    {m.state ?? "Unknown"}
                  </Badge>
                  {m.version !== undefined && (
                    <span className="text-xs text-muted-foreground font-mono shrink-0">
                      v{m.version}
                    </span>
                  )}
                </div>
              ))}
            </div>
          )}
        </Card>

        {/* Detail panel */}
        {selected && (
          <MappingDetailPanel mapping={selected} onRefresh={handleRefresh} />
        )}
      </div>
    </Page>
  );
}
