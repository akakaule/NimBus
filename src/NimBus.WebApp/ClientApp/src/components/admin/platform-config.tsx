import { useState, useEffect } from "react";
import * as api from "api-client";
import { Button } from "components/ui/button";
import { Card, CardContent, CardHeader, CardTitle } from "components/ui/card";
import { Badge } from "components/ui/badge";
import { Spinner } from "components/ui/spinner";

export default function PlatformConfig() {
  const [config, setConfig] = useState<api.PlatformConfig | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [expandedEndpoint, setExpandedEndpoint] = useState<string | null>(null);

  useEffect(() => {
    loadConfig();
  }, []);

  async function loadConfig() {
    setLoading(true);
    setError(null);
    try {
      const client = new api.Client(api.CookieAuth());
      const result = await client.getAdminPlatformConfig();
      setConfig(result);
    } catch (err: any) {
      setError(err.message ?? "Failed to load platform configuration");
    } finally {
      setLoading(false);
    }
  }

  function handleExport() {
    if (!config) return;
    const json = JSON.stringify(config.toJSON(), null, 2);
    const blob = new Blob([json], { type: "application/json" });
    const url = URL.createObjectURL(blob);
    const a = document.createElement("a");
    a.href = url;
    a.download = "platform-config.json";
    a.click();
    URL.revokeObjectURL(url);
  }

  if (loading) {
    return (
      <div className="flex justify-center items-center py-12">
        <Spinner size="lg" />
      </div>
    );
  }

  if (error) {
    return (
      <div className="bg-red-50 border border-red-200 rounded-md p-4 text-red-800">
        {error}
      </div>
    );
  }

  if (!config) return null;

  const endpoints = config.endpoints ?? [];
  const allEventTypes = new Set<string>();
  endpoints.forEach((ep) => {
    (ep.eventTypesProduced ?? []).forEach((et) => {
      if (et.id) allEventTypes.add(et.id);
    });
    (ep.eventTypesConsumed ?? []).forEach((et) => {
      if (et.id) allEventTypes.add(et.id);
    });
  });

  return (
    <div className="space-y-6 w-full">
      <div className="flex justify-between items-center">
        <div className="flex gap-4">
          <Card className="px-4 py-3">
            <div className="text-sm text-muted-foreground">Endpoints</div>
            <div className="text-2xl font-bold">{endpoints.length}</div>
          </Card>
          <Card className="px-4 py-3">
            <div className="text-sm text-muted-foreground">Event Types</div>
            <div className="text-2xl font-bold">{allEventTypes.size}</div>
          </Card>
        </div>
        <Button onClick={handleExport} colorScheme="primary">
          Export JSON
        </Button>
      </div>

      <Card>
        <CardHeader>
          <CardTitle>Endpoints</CardTitle>
        </CardHeader>
        <CardContent className="p-0">
          <table className="w-full text-sm">
            <thead>
              <tr className="border-b bg-muted">
                <th className="text-left p-3 font-medium">Name</th>
                <th className="text-left p-3 font-medium">ID</th>
                <th className="text-center p-3 font-medium">Produces</th>
                <th className="text-center p-3 font-medium">Consumes</th>
              </tr>
            </thead>
            <tbody>
              {endpoints.map((ep) => {
                const isExpanded = expandedEndpoint === ep.id;
                const produced = ep.eventTypesProduced ?? [];
                const consumed = ep.eventTypesConsumed ?? [];

                return (
                  <tr key={ep.id} className="border-b last:border-b-0">
                    <td colSpan={4} className="p-0">
                      <div
                        className="flex items-center cursor-pointer hover:bg-accent p-3"
                        onClick={() =>
                          setExpandedEndpoint(isExpanded ? null : ep.id!)
                        }
                      >
                        <span className="mr-2 text-muted-foreground">
                          {isExpanded ? "▼" : "▶"}
                        </span>
                        <span className="flex-1 font-medium">{ep.name}</span>
                        <span className="flex-1 text-muted-foreground font-mono text-xs">
                          {ep.id}
                        </span>
                        <span className="w-24 text-center">
                          <Badge variant="info" size="sm">
                            {produced.length}
                          </Badge>
                        </span>
                        <span className="w-24 text-center">
                          <Badge variant="secondary" size="sm">
                            {consumed.length}
                          </Badge>
                        </span>
                      </div>
                      {isExpanded && (
                        <div className="px-8 pb-3 bg-muted border-t">
                          <div className="grid grid-cols-2 gap-4 pt-3">
                            <div>
                              <h4 className="text-xs font-semibold text-muted-foreground uppercase mb-2">
                                Produces
                              </h4>
                              {produced.length === 0 ? (
                                <p className="text-xs text-muted-foreground">
                                  None
                                </p>
                              ) : (
                                <ul className="space-y-1">
                                  {produced.map((et) => (
                                    <li
                                      key={et.id}
                                      className="text-xs font-mono"
                                    >
                                      {et.name}{" "}
                                      <span className="text-muted-foreground">
                                        ({et.id})
                                      </span>
                                    </li>
                                  ))}
                                </ul>
                              )}
                            </div>
                            <div>
                              <h4 className="text-xs font-semibold text-muted-foreground uppercase mb-2">
                                Consumes
                              </h4>
                              {consumed.length === 0 ? (
                                <p className="text-xs text-muted-foreground">
                                  None
                                </p>
                              ) : (
                                <ul className="space-y-1">
                                  {consumed.map((et) => (
                                    <li
                                      key={et.id}
                                      className="text-xs font-mono"
                                    >
                                      {et.name}{" "}
                                      <span className="text-muted-foreground">
                                        ({et.id})
                                      </span>
                                    </li>
                                  ))}
                                </ul>
                              )}
                            </div>
                          </div>
                        </div>
                      )}
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </CardContent>
      </Card>
    </div>
  );
}
