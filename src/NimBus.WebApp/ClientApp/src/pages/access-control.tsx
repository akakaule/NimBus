import { useCallback, useEffect, useMemo, useState } from "react";
import * as api from "api-client";
import Page from "components/page";
import { RoleCard } from "components/access-control/role-card";
import { useAccess, invalidateAccess } from "hooks/use-access";
import { useToast } from "components/ui/toast";

type SiteRole = "reader" | "contributor" | "owner" | "piiReader";

const SITE_CARDS: Array<{
  role: SiteRole;
  title: string;
  description: string;
  tone: string;
  pick: (set: api.AccessControlSet) => string[];
}> = [
  {
    role: "reader",
    title: "Readers",
    description: "View endpoints, events, metrics and audits across the platform.",
    tone: "bg-status-info",
    pick: (s) => s.readers ?? [],
  },
  {
    role: "contributor",
    title: "Contributors",
    description: "Everything Readers can, plus resubmit, skip, handoff and compose.",
    tone: "bg-status-success",
    pick: (s) => s.contributors ?? [],
  },
  {
    role: "owner",
    title: "Owners",
    description:
      "Full management: admin operations, purge, endpoint settings, and these access-control lists.",
    tone: "bg-status-danger",
    pick: (s) => s.owners ?? [],
  },
  {
    role: "piiReader",
    title: "PII Readers",
    description:
      "May view raw event payloads. Orthogonal to the ladder — Owners do NOT see payloads without this.",
    tone: "bg-status-warning",
    pick: (s) => s.piiReaders ?? [],
  },
];

const ENDPOINT_CARDS = SITE_CARDS.filter((c) => c.role !== "piiReader");

const toRoleEnum = (role: SiteRole): api.RoleEntryRole =>
  ({
    reader: api.RoleEntryRole.Reader,
    contributor: api.RoleEntryRole.Contributor,
    owner: api.RoleEntryRole.Owner,
    piiReader: api.RoleEntryRole.PiiReader,
  })[role];

export default function AccessControl() {
  const { access } = useAccess();
  const { addToast } = useToast();
  const client = useMemo(() => new api.Client(api.CookieAuth()), []);

  const canManageSite = access?.canManageAccessControl ?? false;
  const ownedEndpoints = useMemo(
    () =>
      (access?.endpointRoles ?? [])
        .filter((r) => r.role === api.EndpointRoleInfoRole.Owner)
        .map((r) => r.endpointId ?? "")
        .filter(Boolean),
    [access],
  );

  const [siteSet, setSiteSet] = useState<api.AccessControlSet | null>(null);
  const [endpointIds, setEndpointIds] = useState<string[]>([]);
  const [selectedEndpoint, setSelectedEndpoint] = useState("");
  const [endpointSet, setEndpointSet] = useState<api.AccessControlSet | null>(null);
  const [busy, setBusy] = useState(false);

  // Site lists (site Owners only).
  useEffect(() => {
    if (!canManageSite) return;
    client.getAccessControl().then(setSiteSet).catch(() => setSiteSet(null));
  }, [canManageSite, client]);

  // Endpoint selector: site Owners manage every endpoint, endpoint Owners
  // only their own.
  useEffect(() => {
    if (canManageSite) {
      client.getEndpointsAll().then((ids) => setEndpointIds(ids ?? [])).catch(() => setEndpointIds([]));
    } else {
      setEndpointIds(ownedEndpoints);
    }
  }, [canManageSite, ownedEndpoints, client]);

  useEffect(() => {
    if (!selectedEndpoint) {
      setEndpointSet(null);
      return;
    }
    client
      .getEndpointAccessControl(selectedEndpoint)
      .then(setEndpointSet)
      .catch(() => setEndpointSet(null));
  }, [selectedEndpoint, client]);

  const mutate = useCallback(
    async (fn: () => Promise<api.AccessControlSet>, apply: (s: api.AccessControlSet) => void) => {
      setBusy(true);
      try {
        apply(await fn());
        invalidateAccess();
      } catch (e) {
        addToast({
          title: "Access-control change failed",
          description: e instanceof Error ? e.message : String(e),
          variant: "error",
        });
      } finally {
        setBusy(false);
      }
    },
    [addToast],
  );

  const siteEntry = (role: SiteRole, entry: string) =>
    new api.RoleEntry({ role: toRoleEnum(role), entry });

  if (access && !canManageSite && ownedEndpoints.length === 0) {
    return (
      <Page title="Access Control">
        <p className="text-muted-foreground text-sm">
          You need Owner access (site-wide or on an endpoint) to manage roles.
          Ask a site Owner to grant it on this page.
        </p>
      </Page>
    );
  }

  return (
    <Page
      title="Access Control"
      subtitle="Storage-backed roles: Reader < Contributor < Owner, plus the orthogonal PII Reader capability. Entries are email addresses or Entra object ids."
    >
      {canManageSite && (
        <section className="mb-8">
          <h2 className="text-sm font-bold uppercase tracking-wider text-muted-foreground mb-3">
            Site-wide roles
          </h2>
          <div className="grid gap-4 md:grid-cols-2">
            {SITE_CARDS.map((card) => (
              <RoleCard
                key={card.role}
                title={card.title}
                description={card.description}
                tone={card.tone}
                entries={siteSet ? card.pick(siteSet) : []}
                busy={busy || !siteSet}
                onAdd={(entry) =>
                  mutate(() => client.postAccessControlRole(siteEntry(card.role, entry)), setSiteSet)
                }
                onRemove={(entry) =>
                  mutate(() => client.deleteAccessControlRole(siteEntry(card.role, entry)), setSiteSet)
                }
              />
            ))}
          </div>
        </section>
      )}

      <section>
        <h2 className="text-sm font-bold uppercase tracking-wider text-muted-foreground mb-3">
          Endpoint roles
        </h2>
        <div className="flex items-center gap-3 mb-4">
          <label htmlFor="ac-endpoint" className="text-[13px] font-medium">
            Endpoint
          </label>
          <select
            id="ac-endpoint"
            value={selectedEndpoint}
            onChange={(e) => setSelectedEndpoint(e.target.value)}
            className="text-[13px] border border-border rounded-nb-sm px-2 py-1.5 bg-background"
          >
            <option value="">Select an endpoint…</option>
            {endpointIds.map((id) => (
              <option key={id} value={id}>
                {id}
              </option>
            ))}
          </select>
        </div>

        {selectedEndpoint && (
          <div className="grid gap-4 md:grid-cols-3">
            {ENDPOINT_CARDS.map((card) => (
              <RoleCard
                key={card.role}
                title={card.title}
                description={
                  card.role === "owner"
                    ? "Manage this endpoint: purge, settings, subscriptions and its role grants."
                    : card.description
                }
                tone={card.tone}
                entries={endpointSet ? card.pick(endpointSet) : []}
                busy={busy || !endpointSet}
                onAdd={(entry) =>
                  mutate(
                    () =>
                      client.postEndpointAccessControlRole(
                        selectedEndpoint,
                        siteEntry(card.role, entry),
                      ),
                    setEndpointSet,
                  )
                }
                onRemove={(entry) =>
                  mutate(
                    () =>
                      client.deleteEndpointAccessControlRole(
                        selectedEndpoint,
                        siteEntry(card.role, entry),
                      ),
                    setEndpointSet,
                  )
                }
              />
            ))}
          </div>
        )}
      </section>
    </Page>
  );
}
