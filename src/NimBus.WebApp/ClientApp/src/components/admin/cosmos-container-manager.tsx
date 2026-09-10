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
  const [pending, setPending] = useState<string>();
  const [deleting, setDeleting] = useState(false);

  const load = useCallback(async () => {
    setLoading(true);
    try {
      setContainers(await client.getAdminCosmosContainers());
      setUnavailable(false);
      setError(undefined);
    } catch (failure: unknown) {
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

  async function deleteContainer() {
    if (!pending) return;
    const name = pending;
    setDeleting(true);
    try {
      await client.postAdminCosmosContainerDelete(
        new api.CosmosContainerDeleteRequest({ confirmation: name }),
        name,
      );
      setPending(undefined);
      await load();
    } catch {
      setError(`Container ${name} could not be deleted.`);
    } finally {
      setDeleting(false);
    }
  }

  return (
    <Card>
      <CardHeader>
        <CardTitle>Cosmos DB containers</CardTitle>
        <CardDescription>
          Containers listed here are not used by the current platform catalog or
          NimBus storage. Deleting one permanently removes all data it contains.
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
        <div className="flex justify-end">
          <Button
            variant="outline"
            colorScheme="gray"
            size="sm"
            onClick={() => void load()}
            disabled={loading}
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
                  <th className="p-3 text-left font-medium">Container</th>
                  <th className="p-3 text-right font-medium">Action</th>
                </tr>
              </thead>
              <tbody>
                {containers.map((container) => (
                  <tr
                    key={container.name}
                    className="border-b border-border/50 last:border-0"
                  >
                    <td className="p-3 font-mono">{container.name}</td>
                    <td className="p-3 text-right">
                      <Button
                        colorScheme="red"
                        variant="outline"
                        size="sm"
                        onClick={() => setPending(container.name)}
                      >
                        Delete
                      </Button>
                    </td>
                  </tr>
                ))}
                {!loading && containers.length === 0 && (
                  <tr>
                    <td
                      colSpan={2}
                      className="p-6 text-center text-muted-foreground"
                    >
                      No containers outside the current platform.
                    </td>
                  </tr>
                )}
                {loading && (
                  <tr>
                    <td
                      colSpan={2}
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
        isOpen={Boolean(pending)}
        onClose={() => setPending(undefined)}
        onConfirm={() => void deleteContainer()}
        title="Delete Cosmos DB Container"
        description={`Permanently delete ${pending ?? "this container"} and every document it contains.`}
        confirmText={pending ?? ""}
        confirmLabel="Permanently delete container"
        caseSensitive
        isLoading={deleting}
      />
    </Card>
  );
}
