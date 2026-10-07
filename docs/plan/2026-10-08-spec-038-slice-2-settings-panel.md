# Spec 038 slice 2: Settings list and settings panel

## Context
Slice 1 ([context-and-oss/NimBus#218](https://github.com/context-and-oss/NimBus/pull/218), branch `claude/admin-page-ui-simplify-4e82d0`) split Admin into Operations, Topology and Settings. Today Settings is a static list, and each row opens an unchanged settings component in `SidePanel`. Slice 2 builds out Spec 038 §7–§8:

- **List:** each row shows its live status and a summary, with an inline quick action where safe. The list gets a filter.
- **Panel frame:** a tabbed, consistent frame with a sticky footer and an unsaved-changes guard.

The components keep their own save logic. The frame standardises only what surrounds them.

Decisions taken (user, 2026-10-08):
- **Back guard:** guard closing inside the app only (✕, Escape, backdrop, switching feature) plus the browser's leave-page prompt (`beforeunload`). Browser Back closes the panel without asking. No router migration.
- **Heartbeat toggles:** per-endpoint include toggles stay immediate. The footer covers only enabled, interval and timeout.
- **Layout:** single column inside the panel. Standalone two-column layouts stay as they are.

Two bugs from slice 1 are fixed here:
- `components/ui/modal.tsx` doesn't call `preventDefault` on Escape. One Escape in the MCP turn-off dialog or the Simulation ownership dialog therefore closes the whole panel too.
- Closing a Modal sets `body.overflow = ""`, which drops the panel's scroll lock.

## Branch and PR
- New branch `claude/settings-panel-frame` from the slice 1 branch's head. The PR is stacked on #218, with base `claude/admin-page-ui-simplify-4e82d0`; retarget it to `master` once #218 merges.
- Write this plan to `docs/plan/2026-10-08-spec-038-slice-2-settings-panel.md` (AGENTS.md) and commit it first.

## Design

### 1. Panel frame context: `components/settings/panel-frame.tsx` (new)
`SettingsPanelContext`, provided by `pages/settings.tsx` and **absent when a component renders standalone**. Every helper falls back to today's behaviour when there's no context, so the existing component tests keep passing unchanged.

| Export | Behaviour inside the frame | Behaviour standalone |
|---|---|---|
| `<PanelSection tab="…">` | Renders children only when `tab` is the active tab and the component isn't reviewing | Always renders |
| `<PanelFooter>` | Portals children into the frame's sticky footer element | Renders inline |
| `usePanelDirty(count)` | Reports the unsaved-change count to the frame (drives the close guard) | No-op |
| `usePanelReviewing(bool)` | While true the frame hides the tab bar, and sections hide, so the review screen replaces the body (§8.3) | No-op |
| `useInPanel()` | Lets a component switch its `xl:grid-cols-[…]` grid to a single column | Returns false |

Portalled submit buttons keep working because they use `form="<form id>"`. MCP's and FI's `<form>`s get stable ids.

### 2. Changes to each component (minimal; save logic unchanged)
| Component | Tabs (`PanelSection`) | Dirty count | Footer moved via `PanelFooter` | Other |
|---|---|---|---|---|
| `mcp-access-settings.tsx` | overview (Tiles + 01), permissions (02 + PreviewCard), who (03), limits (04), connect (ConnectCard), activity (ActivityCard) | existing `changes.length` | existing footer `:266-273` | `usePanelReviewing(reviewing)`; form id |
| `failure-intelligence-settings.tsx` | provider (01), evidence (02 + "What leaves NimBus"), scope (03 + info card), classification (04) | **new** `countChanges(state.saved, draft, redactedKeys, endpoints, selected, apiKey, clearApiKey)` | footer `:168`; add a "N unsaved changes" text; "Review changes" disabled when the count is 0 | `usePanelReviewing(review)`; form id |
| `simulation-settings.tsx` | mode (01), endpoints (02), policy (aside) | **new**: compare `draft` with `toDraft(status)` | footer `:285-295` plus a count text | none |
| `audit-settings.tsx` | one tab, so no tab bar | symmetric difference of `saved` / `disabled` | the existing Discard/Save toolbar buttons | none |
| `heartbeat-card.tsx` | probing (toggle, interval, timeout, Send now), endpoints (filter + table) | **new**: keep `savedSettings`; compare enabled, interval and timeout | **new** Discard plus the existing "Save" | Fix: `sendNow` and Refresh reload only the overview, so they no longer overwrite unsaved settings |

Button labels, aria names, alerts and headings stay the same, so the existing tests keep passing.

### 3. Feature registry: extend `models/manage-pages.ts`
Add to `SettingsFeature`:
- `tabs: {id, label, keywords}[]`
- `keywords`
- `applies` (for example "Applies within 30 s", "Restart required after saving", "Applies on this instance", "Applies immediately", "Applies on the next probe")

The topbar breadcrumb keeps using `name`.

### 4. Row status and summaries: `components/settings/feature-status.ts` (new, pure) + `hooks/use-settings-status.ts` (new)
- **Fetching:** the hook fetches in parallel when the Settings page mounts, and again after a panel closes. It uses the same endpoints the components use:
  - MCP: `/api/admin/mcp/settings` and `/api/admin/mcp/activity?hours=24`
  - Failure intelligence: `/api/admin/failure-intelligence`
  - Simulation: `getAdminSimulation`
  - Audit: `getAdminAuditSettings`
  - Heartbeat: `getAdminHeartbeatSettings` and `getAdminHeartbeatOverview`
  - A failed fetch gives an "Unavailable" row, not a broken page.
- **Status mapping:** pure `toStatus(featureId, data) → {tone, label, summary, attention, quick?}` covers the §7.3 states:
  - MCP: Not set up / Serving / Off
  - Failure intelligence: Restart required / Key unreadable / No API key / On / Off
  - Simulation: Blocked / On / Off
  - Audit: N of M recorded
  - Heartbeat: On / Off, plus included count and interval
  - MCP reuses `Tiles`' existing mapping logic: extract it from `mcp-access-settings.tsx:324-327` into `mcp-access-model.ts` and share it.
- **Quick actions (§7.2 rule):** inline only when the action narrows access and applies immediately.
  - MCP **Turn off now**: a confirm Modal, then `POST /api/admin/mcp/turn-off` with `X-NimBus-CSRF` taken from the loaded state.
  - Heartbeat **Send now**: `postAdminHeartbeatSend`.
  - Simulation **Open Simulate**: a link, shown only when enabled.
  - Everything else ("Turn on…", "Set up…", "Enable…") opens the panel on the matching tab (`?tab=`).

### 5. `pages/settings.tsx`
- **Rows:** badge, description, summary and quick action. Each row stays a link to `/Settings/:id`, and the quick action is a separate button.
- **Filter:**
  - Chips All / On / Off / Needs attention.
  - A text filter over name, description and the feature's and tabs' keywords.
  - Filter state is local, not in the URL.
- **Panel frame:**
  - Header: name, status badge, ✕.
  - Tab bar from the registry, kept in the URL as `?tab=` and switched with `replace`. An unknown tab falls back to the first.
  - No per-tab unsaved-change dot: components report a single total, which drives the guard and the footer.
  - Body with the component, and the sticky footer slot.
  - Panel width `max-w-[880px]` (spec §8.1), now that panels are single column.
- **Guard:**
  - `close()` and switching feature check the dirty count. If it isn't zero, show a `Modal` "Discard N unsaved changes?" with "Keep editing" and "Discard".
  - While dirty, a `beforeunload` listener is active.
  - Browser Back isn't guarded, by decision.

### 6. `components/ui/modal.tsx` fixes
- Call `e.preventDefault()` in the Escape handler, so `SidePanel` ignores the same key press (it already checks `defaultPrevented`).
- Restore the previous `body.style.overflow` instead of `""`.
- Add `aria-labelledby` support, so a test can find the dialog by name inside the panel.

## Critical files
- New: `components/settings/panel-frame.tsx`, `components/settings/feature-status.ts`, `hooks/use-settings-status.ts`, plus tests for each.
- Modified: `pages/settings.tsx`, `models/manage-pages.ts`, `components/ui/modal.tsx`, the five settings components, `mcp-access-model.ts`, `docs/spec/038-manage-pages-split/spec.md` (status line, and note the decisions above).

## Tests (written first, RED, then GREEN)
- **`panel-frame.test.tsx`**
  - A section is hidden when its tab isn't active.
  - The footer is portalled inside the frame and inline standalone.
  - The reviewing flag hides the tabs and sections.
- **`feature-status.test.ts`:** every state in §7.3 maps to the right tone, label, summary and quick action, including the "never widen inline" rule.
- **`pages/settings.test.tsx`** (extended)
  - Rows show status from mocked fetches.
  - The filter narrows rows.
  - Turn off now confirms, then posts with CSRF.
  - Send now posts.
  - "Set up…" opens `?tab=provider`.
  - The tab bar switches sections and the URL.
  - Closing while dirty prompts. "Keep editing" keeps the panel open; "Discard" closes it.
  - Escape inside a nested Modal closes only the Modal.
  - An unknown tab falls back to the first.
- **Component tests** (added; existing ones unchanged)
  - FI, Simulation and Heartbeat report dirty counts.
  - Heartbeat Discard restores the saved values.
  - Heartbeat Send now keeps unsaved interval edits.
- **`modal.test.tsx`:** Escape calls `preventDefault`, and overflow is restored.

## Verification
- `npm --prefix src/NimBus.WebApp/ClientApp run test:ci`, `npm run build` and ESLint on the changed files.
- `dotnet build src/NimBus.sln -c Release`: frontend-only change, so this is a sanity check that NSwag and the SPA build hold.
- **Real UI:** use the Aspire stack already running from this worktree (WebApp https://localhost:23577, local Owner). Before checking out the new branch, check that the stack doesn't block it; restart the `webapp` resource if needed. In the browser pane, go through every feature panel:
  - tabs, the footer, the dirty guard, a real save for each feature, Escape inside nested Modals;
  - the list's statuses and quick actions against real data.
- Capture PR screenshots with neutral demo data (the local stack's demo endpoints), pushed to `pr-assets/settings-panel-frame`.
