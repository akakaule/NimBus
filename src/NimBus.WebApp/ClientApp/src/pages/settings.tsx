import type { ReactNode } from "react";
import { Link, Navigate, useLocation, useNavigate, useParams } from "react-router-dom";
import Page from "components/page";
import { SidePanel } from "components/ui/side-panel";
import McpAccessSettings from "components/admin/mcp-access-settings";
import FailureIntelligenceSettings from "components/admin/failure-intelligence-settings";
import HeartbeatCard from "components/admin/heartbeat-card";
import SimulationSettings from "components/admin/simulation-settings";
import AuditSettings from "components/admin/audit-settings";
import { SETTINGS_FEATURES } from "models/manage-pages";

/** Each feature's existing settings component, keyed by its URL segment. */
const PANELS: Record<string, () => ReactNode> = {
  mcp: () => <McpAccessSettings />,
  "failure-intelligence": () => <FailureIntelligenceSettings />,
  heartbeat: () => <HeartbeatCard />,
  simulation: () => <SimulationSettings />,
  audit: () => <AuditSettings />,
};

const GROUPS = [...new Set(SETTINGS_FEATURES.map((f) => f.group))];

/** Set on rows' links so closing can step back instead of stacking history. */
interface OpenedFromList {
  fromList?: boolean;
}

/**
 * Opt-in capabilities, one row per feature; each opens its existing settings
 * in a side panel at /Settings/:feature. Replaces the Admin tabs for MCP access,
 * Failure intelligence, Simulation, Audit and the heartbeat half of Health.
 * Site Owner only, like every /api/admin/* call the panels make.
 */
export default function Settings() {
  const { feature } = useParams();
  const navigate = useNavigate();
  const location = useLocation();
  const open = SETTINGS_FEATURES.find((f) => f.id === feature?.toLowerCase());

  if (feature && !open) return <Navigate to="/Settings" replace />;

  // Opened from a row: step back, so Back afterwards doesn't reopen the panel.
  // Deep-linked: there is no list entry behind it, so replace instead.
  const close = () => {
    if ((location.state as OpenedFromList | null)?.fromList) navigate(-1);
    else navigate("/Settings", { replace: true });
  };

  return (
    <Page
      title="Settings"
      subtitle="Opt-in capabilities for this deployment."
    >
      <div className="w-full max-w-4xl space-y-6">
        {GROUPS.map((group) => (
          <section key={group} aria-labelledby={`settings-group-${group}`}>
            <h2
              id={`settings-group-${group}`}
              className="m-0 mb-2 ml-0.5 font-mono text-[10.5px] font-medium uppercase tracking-[0.14em] text-muted-foreground"
            >
              {group}
            </h2>
            <ul className="m-0 list-none divide-y divide-border overflow-hidden rounded-nb-md border border-border bg-card p-0">
              {SETTINGS_FEATURES.filter((f) => f.group === group).map((f) => (
                <li key={f.id}>
                  <Link
                    to={`/Settings/${f.id}`}
                    state={{ fromList: true } satisfies OpenedFromList}
                    className="flex items-center gap-4 px-4 py-3.5 text-foreground no-underline transition-colors hover:bg-muted hover:no-underline"
                  >
                    <span className="min-w-0 flex-1">
                      <span className="block text-[15px] font-semibold">{f.name}</span>
                      <span className="block text-sm text-muted-foreground">
                        {f.description}
                      </span>
                    </span>
                    <span className="shrink-0 text-sm font-semibold text-primary">
                      Configure ›
                    </span>
                  </Link>
                </li>
              ))}
            </ul>
          </section>
        ))}
      </div>

      <SidePanel
        isOpen={!!open}
        onClose={close}
        label={open?.name ?? "Settings"}
        className="max-w-[1120px]"
      >
        {open && (
          <>
            <header className="flex items-center justify-between gap-4 border-b border-border px-6 py-3">
              <div className="font-mono text-[11px] text-muted-foreground">
                Settings / {open.name}
              </div>
              <button
                type="button"
                aria-label={`Close ${open.name}`}
                onClick={close}
                className="inline-flex h-8 w-8 items-center justify-center rounded-nb-sm text-foreground hover:bg-muted"
              >
                <svg
                  width="16"
                  height="16"
                  viewBox="0 0 24 24"
                  fill="none"
                  stroke="currentColor"
                  strokeWidth="2.2"
                  strokeLinecap="round"
                  aria-hidden="true"
                >
                  <path d="M6 6l12 12M18 6L6 18" />
                </svg>
              </button>
            </header>
            <div className="min-h-0 flex-1 overflow-y-auto p-6">{PANELS[open.id]()}</div>
          </>
        )}
      </SidePanel>
    </Page>
  );
}
