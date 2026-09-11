import { useCallback, useEffect, useMemo, useState } from "react";
import * as api from "api-client";
import { Button } from "components/ui/button";
import {
  Card,
  CardContent,
  CardDescription,
  CardHeader,
  CardTitle,
} from "components/ui/card";
import ConfirmDestructiveAction from "./confirm-destructive-action";

export default function CosmosContainerManager() {
  const client = useMemo(() => new api.Client(api.CookieAuth()), []);
  const [containers, setContainers] = useState<api.CosmosContainerInfo[]>([]);
  const [loading, setLoading] = useState(true);
  const [unavailable, setUnavailable] = useState(false);
  const [error, setError] = useState<string>();
  const [pending, setPending] = useState<string[]>([]);
  const [selected, setSelected] = useState<string[]>([]);
  const [filter, setFilter] = useState("all");
  const visible = containers.filter(
    (container) =>
      filter === "all" ||
      (filter === "platform"
        ? container.isInPlatform === true
        : container.isInPlatform === false),
  );
  const selectable = visible.filter(
    (container) => container.isInPlatform === false,
  );
  const allSelected =
    selectable.length > 0 &&
    selectable.every((container) => selected.includes(container.name));
  const [deleting, setDeleting] = useState(false);

  const load = useCallback(async () => {
    setLoading(true);
    setSelected([]);
    try {
      setContainers(await client.getAdminCosmosContainers());
      setUnavailable(false);
      setError(undefined);
    } catch (failure: unknown) {
      setContainers([]);
      const status = (failure as { status?: number } | null)?.status;
      if (status === 404) {
        setUnavailable(true);
        setError(undefined);
      } else {
        setError("Cosmos DB containers could not be loaded.");
      }
    } finally {
      setLoading(false);
    }
  }, [client]);

  useEffect(() => {
    void load();
  }, [load]);

  async function deleteContainers() {
    if (pending.length === 0 || deleting) return;
    const names = [...pending];
    const failed: string[] = [];
    setDeleting(true);
    setError(undefined);
    try {
      for (const name of names) {
        try {
          const result = await client.postAdminCosmosContainerDelete(
            new api.CosmosContainerDeleteRequest({ confirmation: name }),
            name,
          );
          if (!result.deleted) failed.push(name);
        } catch {
          failed.push(name);
        }
      }
      setPending([]);
      await load();
      if (failed.length > 0) {
        const message = `Deleted ${names.length - failed.length} of ${names.length} containers. Could not delete: ${failed.join(", ")}.`;
        setError((loadError) =>
          loadError ? `${message} ${loadError}` : message,
        );
      }
    } finally {
      setDeleting(false);
    }
  }

  return (
    <Card>
      <CardHeader>
        <CardTitle>Cosmos DB containers</CardTitle>
        <CardDescription>
          Containers outside the current platform are highlighted. Platform
          catalog and internal NimBus containers are protected. Deleting a
          container permanently removes all data it contains.
        </CardDescription>
      </CardHeader>
      <CardContent className="space-y-4">
        {error && (
          <div
            role="alert"
            className="rounded-nb-md border border-status-danger/30 bg-status-danger-50 p-3 text-sm text-status-danger-ink"
          >
            {error}
          </div>
        )}
        <div className="flex flex-wrap items-center gap-3">
          <label className="flex items-center gap-2 text-sm">
            Platform membership
            <select
              className="rounded-nb-md border border-border bg-background p-2 text-foreground"
              value={filter}
              disabled={loading || deleting}
              onChange={(event) => {
                setFilter(event.target.value);
                setSelected([]);
              }}
            >
              <option value="all">All containers</option>
              <option value="platform">In platform</option>
              <option value="outside">Not in platform</option>
            </select>
          </label>
          <Button
            className="ml-auto"
            colorScheme="red"
            variant="outline"
            size="sm"
            disabled={loading || deleting || selected.length === 0}
            onClick={() => setPending([...selected])}
          >
            Delete selected ({selected.length})
          </Button>
          <Button
            variant="outline"
            colorScheme="gray"
            size="sm"
            onClick={() => void load()}
            disabled={loading || deleting}
          >
            Refresh
          </Button>
        </div>
        {unavailable ? (
          <p className="text-sm text-muted-foreground">
            Cosmos DB storage is not configured for this WebApp.
          </p>
        ) : (
          <div className="overflow-hidden rounded-nb-md border border-border">
            <table className="w-full text-sm">
              <thead>
                <tr className="border-b bg-muted">
                  <th className="w-10 p-3">
                    <input
                      type="checkbox"
                      aria-label="Select all visible non-platform containers"
                      checked={allSelected}
                      ref={(element) => {
                        if (element)
                          element.indeterminate =
                            selected.length > 0 && !allSelected;
                      }}
                      disabled={loading || deleting || selectable.length === 0}
                      onChange={(event) =>
                        setSelected(
                          event.target.checked
                            ? selectable.map((container) => container.name)
                            : [],
                        )
                      }
                    />
                  </th>
                  <th className="p-3 text-left font-medium">Container</th>
                  <th className="p-3 text-left font-medium">
                    Platform membership
                  </th>
                  <th className="p-3 text-right font-medium">Action</th>
                </tr>
              </thead>
              <tbody>
                {visible.map((container) => (
                  <tr
                    key={container.name}
                    className={`border-b border-border/50 last:border-0 ${container.isInPlatform === false ? "bg-status-warning-50 dark:bg-amber-950/20" : ""}`}
                  >
                    <td className="p-3">
                      <input
                        type="checkbox"
                        aria-label={`Select ${container.name}`}
                        checked={selected.includes(container.name)}
                        disabled={
                          loading ||
                          deleting ||
                          container.isInPlatform !== false
                        }
                        onChange={(event) =>
                          setSelected((current) =>
                            event.target.checked
                              ? [...current, container.name]
                              : current.filter(
                                  (name) => name !== container.name,
                                ),
                          )
                        }
                      />
                    </td>
                    <td className="p-3 font-mono">{container.name}</td>
                    <td className="p-3">
                      <span
                        className={`inline-flex rounded-full px-2 py-1 text-xs font-medium ${container.isInPlatform === false ? "bg-status-warning-50 text-status-warning-ink dark:bg-amber-950/40 dark:text-amber-200" : "bg-muted text-muted-foreground"}`}
                      >
                        {container.isInPlatform === true
                          ? "In platform"
                          : container.isInPlatform === false
                            ? "Not in platform"
                            : "Unknown"}
                      </span>
                    </td>
                    <td className="p-3 text-right">
                      {container.isInPlatform === false ? (
                        <Button
                          colorScheme="red"
                          variant="outline"
                          size="sm"
                          disabled={loading || deleting}
                          onClick={() => setPending([container.name])}
                        >
                          Delete
                        </Button>
                      ) : (
                        <span className="text-muted-foreground">Protected</span>
                      )}
                    </td>
                  </tr>
                ))}
                {!loading && visible.length === 0 && (
                  <tr>
                    <td
                      colSpan={4}
                      className="p-6 text-center text-muted-foreground"
                    >
                      No containers match this filter.
                    </td>
                  </tr>
                )}
                {loading && (
                  <tr>
                    <td
                      colSpan={4}
                      className="p-6 text-center text-muted-foreground"
                    >
                      Loading…
                    </td>
                  </tr>
                )}
              </tbody>
            </table>
          </div>
        )}
      </CardContent>
      <ConfirmDestructiveAction
        isOpen={pending.length > 0}
        onClose={() => {
          if (!deleting) setPending([]);
        }}
        onConfirm={() => void deleteContainers()}
        title={
          pending.length > 1
            ? "Delete Cosmos DB Containers"
            : "Delete Cosmos DB Container"
        }
        description={`Permanently delete ${pending.length > 1 ? `${pending.length} containers: ${pending.join(", ")}` : (pending[0] ?? "this container")} and every document they contain.`}
        confirmText={
          pending.length > 1
            ? `DELETE ${pending.length} CONTAINERS`
            : (pending[0] ?? "")
        }
        confirmLabel={
          pending.length > 1
            ? `Permanently delete ${pending.length} containers`
            : "Permanently delete container"
        }
        caseSensitive
        isLoading={deleting}
      />
    </Card>
  );
}
