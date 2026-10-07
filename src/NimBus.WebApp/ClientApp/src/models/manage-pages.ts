// URL segments and names for the Manage pages (Spec 038 §5.2). Kept apart from
// the lazily loaded pages so the topbar can name a route without bundling them.

/** Topology views in tab order; each has its own URL: /Topology/:view. */
export const TOPOLOGY_VIEWS = [
  { id: "subscriptions", label: "Subscriptions" },
  { id: "drift", label: "Catalog drift" },
  { id: "storage", label: "Storage" },
] as const;

export interface SettingsFeature {
  /** URL segment: /Settings/:id. */
  id: string;
  group: string;
  name: string;
  description: string;
}

/** Opt-in features in display order. A new feature adds an entry here, not a tab. */
export const SETTINGS_FEATURES: readonly SettingsFeature[] = [
  {
    id: "mcp",
    group: "AI & agents",
    name: "MCP access",
    description:
      "Let AI agents check endpoint health, investigate failed messages and recover them through /mcp.",
  },
  {
    id: "failure-intelligence",
    group: "AI & agents",
    name: "Failure intelligence",
    description:
      "On-demand advisory analysis of failed messages. You control the provider, evidence and endpoint scope.",
  },
  {
    id: "heartbeat",
    group: "Monitoring",
    name: "Heartbeat probing",
    description: "Which endpoints are probed, how often, and how long to wait for a reply.",
  },
  {
    id: "simulation",
    group: "Testing",
    name: "Simulation",
    description:
      "Drive synthetic traffic through the real platform from this WebApp. Never available in production.",
  },
  {
    id: "audit",
    group: "Compliance",
    name: "Audit logging",
    description: "Which operator actions are written to the audit log and Application Insights.",
  },
];
