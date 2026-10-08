# Spec 038 slice 3: Operations redesign

## Context
Slice 1 (#218, merged) made **Operations** its own page. Today it is the endpoint kill-switch card followed by the four accordion groups of operation cards that Admin had, with all forms expanded. Slice 3 builds out Spec 038 §6:

- a status strip;
- an Endpoints table that merges the kill switch with per-endpoint counts and row actions;
- a master/detail operation list, one form at a time, with URLs and a pre-filled endpoint;
- an "N paused" sidebar badge.

The card components keep their own forms, previews and API calls.

Decisions (user, 2026-10-08):
- **Delete single event** gains the typed confirmation every other destructive operation has. Today it deletes on one click.
- **Confirm dialogs:** Recovery operations get a primary-coloured confirm button with a plain label ("Resubmit", "Skip", "Purge session", "Repair …"). Cleanup, data-movement and irreversible operations stay red, with clearer labels instead of today's "Permanently messages".

## Branch and PR
- New branch `claude/operations-redesign`, cut from `origin/master`. It is independent of slice 2 (#219), which touches no Operations files.
- Commit this plan first as `docs/plan/2026-10-08-spec-038-slice-3-operations.md`.
- Update the spec at the end:
  - the status line;
  - correct §6.4, which wrongly says every operation already confirms; single delete gains it here;
  - the confirm-tone decision;
  - "paused for X" can't be shown, because no disabled-since timestamp exists.

## Design

### 1. Operation registry: `models/manage-pages.ts`
- Add `OPERATION_GROUPS` (`recovery`, `cleanup`, `movement`, `danger`), each with a label, caption and tone.
- Add `OPERATIONS` with `{id, group, label, description, takesEndpoint}`. The ids follow spec §5.2: `resubmit, skip, session, stale, dlq, status, to, single, purge, copy, all`.
- The group "Infrastructure" is renamed **Data movement** (§6.3).
- The topbar uses the registry for its sub-crumb, e.g. `Operations / Bulk resubmit failed`.

### 2. Routing
- `app.tsx`: change `/Operations` to `/Operations/:operation?`.
- An unknown operation redirects to `/Operations`. The default operation is `resubmit`.
- `?endpoint=` pre-fills the endpoint.
- Selecting an operation pushes a history entry. Changing the endpoint inside a form doesn't touch the URL.

### 3. Pre-fill: cards get `initialEndpoint?: string`
- Applies to every card except `DeleteMessagesByToCard`, which has no endpoint: `useState<string[]>(initialEndpoint ? [initialEndpoint] : [])`.
- Files: `bulk-operations.tsx`, `advanced-operations.tsx`, `session-management.tsx`, `stale-pending-reconcile.tsx`.
- The detail pane renders the card with `key={`${operation}:${endpoint}`}`, so a new pre-fill remounts the form cleanly.
- The stale-pending tests that pick the endpoint from the Combobox still pass when no `initialEndpoint` is given.

### 4. Confirm dialogs: `confirm-destructive-action.tsx`
- Add `tone?: "danger" | "primary"`, defaulting to `"danger"`. The button colour follows it.
- Recovery cards pass `tone="primary"` and a plain `confirmLabel`. Other cards pass clearer `confirmLabel`s, e.g. "Delete dead-lettered", "Purge subscription", "Delete all events".
- **`DeleteEventCard`:** "Delete Event" opens `ConfirmDestructiveAction` with `confirmText` = the event id, instead of deleting at once. That's the user's decision. The existing ownership and API logic is unchanged.

### 5. Endpoints table: rework `endpoint-controls.tsx`
- `EndpointControlsCard` becomes the page's **Endpoints** table.
- **Props:** `endpoints`, `counts` (from `getEndpointStatusCountAll`, matched by id), and `onOperate(operationId, endpointId)`.
- **Columns:** Endpoint, Receive, Send, Failed, Dead-letter, Pending, plus row actions.
- **Receive and Send use the `ui/toggle` switch** (spec §6.2), as the Endpoints list's row actions already do (`endpoint-list/endpoint-row-actions.tsx`). The existing confirm modal and its text stay as they are for disabling. Enabling applies immediately, as today.
- **Counts:** `failedCount` already includes dead-lettered, and `pendingCount` already includes unsupported (`Mapper.cs:272-286`). So:
  - Failed shows `failedCount`;
  - Dead-letter shows `deadletterCount`;
  - Pending shows `pendingCount − unsupportedCount`;
  - missing counts show "—".
- **Row actions:**
  - *Resubmit…* appears when failed is above zero.
  - A "⋯" `dropdown-menu` lists the other endpoint operations.
  - Both call `onOperate`, which navigates to `/Operations/:op?endpoint=:id`.
- **Filter:** a text input plus a **Needs attention** chip, which keeps rows with a pause, a failure or a dead letter. Those rows get a warning-coloured left stripe.
- **Status lifted out:** the receive and send rows it loads (`refreshEndpoint`) are reported up through `onStatusChange`, so the status strip can count paused endpoints.

### 6. Status strip: `components/admin/operations-status.tsx` (new)
Four `ui/stat-tile` buttons:

| Tile | Value | Click |
|---|---|---|
| Failed | `failureBacklog(counts)`, the same figure as the sidebar's Failed badge | `/Operations/resubmit` |
| Dead-lettered | sum of `deadletterCount` | `/Operations/dlq` |
| Endpoints paused | receive or send disabled, from the table's loaded status | turns on Needs attention |
| Pending | sum of `pendingCount − unsupportedCount` | `/Operations/stale` |

The counts come from one `getEndpointStatusCountAll` call when the page mounts, and again after an operation finishes. Cards get an `onCompleted` callback where they already report results; if that's too invasive, the strip refetches when the selected operation changes.

### 7. Operations page: `components/admin/operations.tsx`
- **Layout:** status strip, then the Endpoints table, then a two-column master/detail.
  - Left: the operation list grouped by `OPERATION_GROUPS`. Each group has a coloured marker and its caption, and the current operation is highlighted (`aria-current`).
  - Right: the card for the selected operation, under a header with its name, group badge and description.
- **Below `lg`:** the list becomes a `<select aria-label="Operation">`.
- **Removed:** the accordion, and `operation-group.tsx`, whose only user was this page.

### 8. Sidebar badge: `components/sidebar.tsx`
- `useFailedBacklog` returns the counts it already polls every 60 s. **There is no extra request.**
- **Operations:**
  - gets `matchPrefix: "/Operations"`;
  - shows a warning pill "N paused" (`aria-label="N endpoints paused"`), counting `subscriptionStatus === "disabled"` only;
  - doesn't count `null` or other values (spec §5.1);
  - doesn't count send-disabled, because send status isn't in the payload.
- The pill uses a new `badgeTone: "warning"` alongside the existing primary `badge`.

## Critical files
- **Modified:**
  - `app.tsx`, `components/topbar.tsx`, `components/sidebar.tsx`, `models/manage-pages.ts`
  - `components/admin/operations.tsx`, `components/admin/endpoint-controls.tsx`
  - the four card files, `components/admin/confirm-destructive-action.tsx`
  - `pages/operations.test.tsx`, `docs/spec/038-manage-pages-split/spec.md`
- **New:** `components/admin/operations-status.tsx`, plus tests.
- **Deleted:** `components/admin/operation-group.tsx`.

## Tests (written first, RED, then GREEN)
- **`pages/operations.test.tsx`** (rewritten)
  - The default operation is resubmit.
  - `/Operations/dlq` deep-links.
  - `?endpoint=` pre-fills the card (via a mocked card that echoes the prop).
  - An unknown operation redirects.
  - List clicks push the URL.
  - The groups include **Data movement**.
  - The strip's values come from the mocked counts; the tile clicks navigate; Paused turns on Needs attention.
- **`endpoint-controls.test.tsx`** (updated and extended)
  - Disabling send via its toggle still asks for confirmation and then calls `postEndpointSendstatus('disable')`.
  - Enabling receive applies immediately.
  - The count columns show the right values, including the overlap arithmetic.
  - Resubmit… calls `onOperate`.
  - The filter and the Needs attention chip work.
- **`confirm-destructive-action.test.tsx`** (new): the `tone` sets the button colour, the label is used, and the button stays disabled until the text matches.
- **Delete single event:** a new test checks that it confirms before calling `deleteAdminEvent`.
- **Cards:** a test that `initialEndpoint` pre-selects the Combobox for one representative card. The stale-pending suite stays unchanged.
- **Sidebar** (`sidebar.failed-badge.test.tsx` extended)
  - The "2 paused" badge counts only `"disabled"`; null or missing values aren't counted.
  - No badge when nothing is paused.
  - Operations stays highlighted on `/Operations/dlq`.
- **`topbar.test.tsx`:** the `/Operations/resubmit` crumb.

## Verification
- `npm run test:ci`, `npm run build` and ESLint on the changed files.
- `dotnet build -c Release` only if a non-frontend file changes; none is planned.
- **Real UI:**
  - If Docker is healthy again, use the Aspire stack from this worktree.
  - Otherwise use the scratchpad mock API and Vite setup from slice 2, extended with endpoint status counts and the per-endpoint status, enable/disable and preview endpoints.
  - Check in the browser pane: the strip, the table (toggles, confirm, counts, filter), row action → pre-filled form, the list, deep links, a typed confirm for both tones, single-delete confirmation, and the sidebar badge.
- PR screenshots with neutral demo data on `pr-assets/operations-redesign`.
