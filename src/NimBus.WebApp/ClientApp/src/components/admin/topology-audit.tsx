import { useState, useEffect } from "react";
import * as api from "api-client";
import { Button } from "components/ui/button";
import { Badge } from "components/ui/badge";
import { Spinner } from "components/ui/spinner";
import { Combobox } from "components/ui/combobox";
import ConfirmDestructiveAction from "./confirm-destructive-action";
import OperationProgress from "./operation-progress";

interface EndpointOption {
  value: string;
  label: string;
}

export default function TopologyAudit() {
  const [endpoints, setEndpoints] = useState<EndpointOption[]>([]);
  const [selectedEndpoint, setSelectedEndpoint] = useState<string[]>([]);
  const [audit, setAudit] = useState<api.TopologyAuditResult | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [showConfirm, setShowConfirm] = useState(false);
  const [cleaning, setCleaning] = useState(false);
  const [cleanupResult, setCleanupResult] =
    useState<api.TopologyCleanupResult | null>(null);

  useEffect(() => {
    loadEndpoints();
  }, []);

  async function loadEndpoints() {
    try {
      const client = new api.Client(api.CookieAuth());
      const config = await client.getAdminPlatformConfig();
      const eps = (config.endpoints ?? []).map((ep) => ({
        value: ep.name ?? ep.id ?? "",
        label: ep.name ?? ep.id ?? "",
      }));
      setEndpoints(eps);
    } catch {
      // Endpoints will be empty
    }
  }

  async function handleAudit() {
    if (selectedEndpoint.length === 0) return;
    setLoading(true);
    setError(null);
    setAudit(null);
    setCleanupResult(null);
    try {
      const client = new api.Client(api.CookieAuth());
      const result = await client.getAdminTopology(selectedEndpoint[0]);
      setAudit(result);
    } catch (err: any) {
      setError(err.message ?? "Failed to audit topology");
    } finally {
      setLoading(false);
    }
  }

  async function handleRemoveDeprecated() {
    if (selectedEndpoint.length === 0) return;
    setCleaning(true);
    setShowConfirm(false);
    try {
      const client = new api.Client(api.CookieAuth());
      const result = await client.postAdminTopologyRemoveDeprecated(
        selectedEndpoint[0],
      );
      setCleanupResult(result);
      // Refresh audit without clearing cleanupResult
      setLoading(true);
      try {
        const auditClient = new api.Client(api.CookieAuth());
        const auditResult = await auditClient.getAdminTopology(
          selectedEndpoint[0],
        );
        setAudit(auditResult);
      } finally {
        setLoading(false);
      }
    } catch (err: any) {
      setError(err.message ?? "Failed to remove deprecated items");
    } finally {
      setCleaning(false);
    }
  }

  return (
    <div className="space-y-6 w-full">
      <div className="flex gap-4 items-end">
        <div className="flex-1 max-w-md">
          <Combobox
            options={endpoints}
            value={selectedEndpoint}
            onChange={setSelectedEndpoint}
            placeholder="Select endpoint..."
            label="Endpoint"
            multiple={false}
          />
        </div>
        <Button
          onClick={handleAudit}
          disabled={selectedEndpoint.length === 0 || loading}
          isLoading={loading}
        >
          Audit Topology
        </Button>
      </div>

      {error && (
        <div className="bg-red-50 border border-red-200 rounded-md p-4 text-red-800">
          {error}
        </div>
      )}

      {audit && (
        <div className="space-y-4">
          <div className="flex justify-between items-center">
            <h3 className="text-lg font-semibold">
              Topic: {audit.topicName}
              {audit.hasDeprecated && (
                <Badge variant="warning" size="sm" className="ml-2">
                  Has deprecated items
                </Badge>
              )}
            </h3>
            {audit.hasDeprecated && (
              <Button
                colorScheme="red"
                onClick={() => setShowConfirm(true)}
                disabled={cleaning}
                isLoading={cleaning}
              >
                Remove Deprecated
              </Button>
            )}
          </div>

          <div className="border rounded-md overflow-hidden">
            {(audit.subscriptions ?? []).map((sub) => (
              <div key={sub.name} className="border-b last:border-b-0">
                <div
                  className={`flex items-center gap-2 px-4 py-2 font-medium text-sm ${
                    sub.isDeprecated ? "bg-red-50 text-red-700" : "bg-muted"
                  }`}
                >
                  <span className="font-mono">{sub.name}</span>
                  {sub.isDeprecated && (
                    <Badge variant="error" size="sm">
                      deprecated
                    </Badge>
                  )}
                </div>
                <div className="pl-8">
                  {(sub.rules ?? []).map((rule) => (
                    <div
                      key={`${sub.name}-${rule.name}`}
                      className={`flex items-center gap-2 px-4 py-1.5 text-sm border-t ${
                        rule.isDeprecated ? "bg-red-50 text-red-600" : ""
                      }`}
                    >
                      <span className="text-muted-foreground">├─</span>
                      <span className="font-mono">{rule.name}</span>
                      {rule.isDeprecated && (
                        <Badge variant="error" size="sm">
                          deprecated
                        </Badge>
                      )}
                    </div>
                  ))}
                </div>
              </div>
            ))}
          </div>

          {cleanupResult && (
            <OperationProgress
              processed={
                (cleanupResult.deletedSubscriptions?.length ?? 0) +
                (cleanupResult.deletedRules?.length ?? 0) +
                (cleanupResult.errors?.length ?? 0)
              }
              succeeded={
                (cleanupResult.deletedSubscriptions?.length ?? 0) +
                (cleanupResult.deletedRules?.length ?? 0)
              }
              failed={cleanupResult.errors?.length ?? 0}
              errors={cleanupResult.errors}
              isComplete={true}
            />
          )}
        </div>
      )}

      <ConfirmDestructiveAction
        isOpen={showConfirm}
        onClose={() => setShowConfirm(false)}
        onConfirm={handleRemoveDeprecated}
        title="Remove Deprecated Topology"
        description={`This will delete all deprecated subscriptions and rules from the Service Bus topic "${selectedEndpoint[0] ?? ""}".`}
        confirmText={selectedEndpoint[0] ?? ""}
        isLoading={cleaning}
      />
    </div>
  );
}
