# Failed page: Application Insights layout and transaction details panel

Status: implemented (2026-09-29) on branch `claude/failed-page-insights-mockup-7c76e8`.

## Implementation notes

What shipped differs from the plan below in these places:

- **One branch, five commits.** The three PRs landed as three `feat(webapp)` commits, then two
  follow-up fixes from a live run against the local Aspire stack.
- **Brush.** The recharts `Brush` sits in its own small chart under the main one, with a
  panorama of the totals. A `Brush` inside the main chart would zoom it, and the chart must keep
  the whole range. Drag steps settle for 350 ms before the list reloads.
- **Filter pills.** Endpoint and event-type picks apply at once, because the combobox's options
  list covered the popover's Apply button. Only From and To get removable pills. The search box
  shows the ID and error-text fields as its own text (`session:s1 503`), so it needs no echo
  line.
- **Reloads.** A pill, legend tile or brush change reloads through the URL only. The search box
  bumps `refresh` only when the query is unchanged. Doing both made the list load twice.
- **Tabs.** The list tab is named *Failures*, not *Individual items*, because *View as* already
  uses that name for items versus table.
- **Table view.** Rows still navigate to Event Details. `DataTable` has no row-click hook, so
  only the item view opens the panel.
- **Flow colours.** The gantt has its own hex `FLOW_COLORS` in `functions/flow-gantt.functions.ts`.
  `MESSAGE_COLORS` in `flow-timeline.tsx` holds Tailwind classes, not colours, so it was not
  moved. Read audits (`get…`, `search…`) are left out of the flow.
- **Side panel.** It focuses the dialog itself rather than its first control. The Tab trap
  cycles through every focusable control.

Verification (2026-09-29):

- `npm run test:ci`: 71 files, 527 tests pass. `npm run lint`: 0 errors; the 14 warnings are
  pre-existing, none in changed files. `npm run build` passes.
- `dotnet build src/NimBus.sln -c Release`: 0 errors on the second run. The first run of a
  reused worktree hit MSB3030 (stale SPA assets), as expected.
  `dotnet test tests/NimBus.WebApp.Tests -c Release --no-build`: 718 passed, 2 skipped.
- A live run on the local Aspire stack (SQL storage, SB emulator) with failures from the
  AspirePubSub publisher. It exercised:
  - the pills, the search box and a brush drag
  - opening the panel from an item and from a deep link
  - Next, and Escape returning focus to the item
  - light and dark themes

The Failed page (`/Failed`) today stacks five stat tiles, a histogram, a nine-field filter grid,
a view toggle and a `DataTable`. This plan restyles it after Application Insights' *Transaction
search*. It also adds an *End-to-end transaction details* panel that opens a failure in place.

The mockup is a Design canvas with two artboards: *Failed — Transaction-search layout* and
*Failed — Transaction details panel*. Screens, copy and data in this plan refer to it.

## Goals

- Filtering takes one row of pills and a single search box, not a nine-field form.
- The totals, the chart and the time window read as one unit: headline, stacked bars, range
  brush and legend tiles.
- Each failure reads as an item: time and status, error text, then its identifiers.
- Opening a failure no longer leaves the list. A side panel shows its message flow, exception,
  properties, blocked siblings and payload, with Resubmit and Skip.

## Non-goals

- No API, storage-provider or `api-spec.yaml` changes. Everything below uses existing
  endpoints (verified in *Constraints*).
- No change to Event Details (`/Message/Index/...`). The panel links to it for anything it
  doesn't cover.
- No free-text OR search across fields on the server (see Decision 3).

## Mapping the design onto today's page

| Design element | Today | Source |
|---|---|---|
| Filter pills: Local time, View as, Status, Endpoint, Event type, add filter (+) | Period segmented control plus the `FailedFilterBar` grid | `pages/failed-messages.tsx:519-557`, `components/failed-messages/failed-filter-bar.tsx` |
| Full-width search box | Event ID, Last message ID, Session ID and Error contains inputs | `failed-filter-bar.tsx` (placeholders at lines 130, 142, 154, 188) |
| Headline "30 unresolved failures between …", Group by | `StatTile` Total, plus the By status / By endpoint split toggle | `failed-messages.tsx:559-565`, `620-639` |
| Stacked bars and range brush | `FailedHistogram` with click-a-bar (`bucket`) | `components/failed-messages/failed-histogram.tsx`, `functions/failed-messages.functions.ts:157` |
| Legend tiles (Failed, DeadLettered, Unsupported, Endpoints affected) | Four `StatTile`s, plus the status toggles in the filter bar | `failed-messages.tsx:566-601`, `failed-filter-bar.tsx:196` |
| Tabs: Individual items / Group by endpoint / Group by error | List / By endpoint / By error | `failed-messages.tsx:692-741` |
| Item rows with bulk Resubmit/Skip, Hide reported and Load more | `DataTable` with checkboxes, `headActions`, `bodyActions`, continuation paging | `failed-messages.tsx:314-343`, `349-478`, `771-788` |
| Transaction details panel | Navigates to Event Details (`route` on each row) | `failed-messages.tsx:354`, `pages/event-details.tsx` |

## Constraints verified in the code

- **The histogram already takes a custom window.** `FailedHistogramRequest` has `from`/`to`
  besides `period` (`api-spec.yaml`, `FailedHistogramRequest`). `FailedImplementation.ResolveWindow`
  validates it and caps it at `MaxWindow`
  (`Controllers/ApiContract/FailedImplementation.cs:105-107`).
- **The list window is client-side.** `FailedSearchFilter.updatedAtFrom/updatedAtTo` narrow the
  list, and `toFailedSearchFilter` already sets them from a window
  (`failed-messages.functions.ts:151-155`).
- **The list order is fixed.** `GetFailedEventsAcrossEndpoints` returns rows newest-first by
  `UpdatedAt` (`IMessageTrackingStore.cs:174-188`), and the request has no sort field. A
  working "Sort by" control would need a store change in all three providers.
- **Only counts are measured.** `FailedHistogram` counts rows. There is no second measure for
  "Measure by".
- **`from` and `to` are already taken.** In `FailedFilterValues` they mean the publisher and
  subscriber endpoints (`failed-messages.functions.ts:81-95`). A time window needs other names.
- **Filters persist in sessionStorage.** `useUrlFilters` mirrors non-default applied values to
  `sessionStorage[persistKey]` (`hooks/use-url-filters.ts:179-193`, key `nimbus.failed.filters`).
  Panel state placed in `FailedFilterValues` would reopen the panel after a reload.
- **Messages have no duration of their own.** `Message` has `enqueuedTimeUtc` but no timing.
  Only `Event` carries `queueTimeMs`, `processingTimeMs`, `retryCount` and `retryLimit`, and
  they describe the last attempt (`api-spec.yaml`, `Event`).
- **The panel's data is already exposed.** Event Details loads the same data through
  `getEventId(id, endpointId)`, `getEventDetailsHistoryId(id, endpointId)`,
  `getMessageAuditsEventId(id)` and `getEventBlockedId(endpointId, sessionId, skip, take)`
  (`pages/event-details.tsx:88-150`).
- **Reusable parts exist.**
  - `IntelligenceCard` (`components/event-details/intelligence-card.tsx`) takes endpoint, event,
    message ID and status.
  - `TimingBar` (`components/ui/timing-bar.tsx`) draws queue and processing segments.
  - `FlowTimeline` has the message-type colour map (`components/event-details/flow-timeline.tsx:28-42`).
  - `Modal` handles Escape and scroll lock (`components/ui/modal.tsx`).
  - recharts 3 ships `Brush` (`node_modules/recharts/types/cartesian/Brush.d.ts`).

## Decisions

1. **Frontend only.** Every element maps onto existing endpoints. Where it doesn't, the element
   is dropped (below) rather than growing the API.
2. **Drop "Measure by". "Sort by" is a label only.** The header says "Newest first (by last
   failure)" without a dropdown, because a sort control the store can't honour would mislead.
   Server-side sort is a follow-up, if anyone asks for it.
3. **The search box maps to the existing fields.**
   - `event:`, `message:` and `session:` prefixes set `eventId`, `lastMessageId` and `sessionId`.
   - A bare GUID sets `eventId`.
   - Anything else sets `errorText`.

   The box echoes how it read the query ("Searching error text for …"). A server-side OR
   `query` field would need every provider and the conformance suite (AGENTS.md, *Gotchas*),
   so it's deferred.
4. **Time window: `windowStart` and `windowEnd`.** These URL params come from the brush. Clicking
   a bar sets them to that bucket. The legacy `bucket` param stays readable: a shared link with
   `bucket=` resolves to the same window. The chart keeps the whole period, and the window
   narrows only the list and error groups, as `bucket` does today.
5. **The panel's URL state lives outside `useUrlFilters`.**
   - `?open=<endpointId>/<eventId>` is read and written with `useSearchParams`, so sessionStorage
     never stores it.
   - Deep links open the panel.
   - Escape and the close button remove the param.
6. **Keep the table as an option.** The "View as" pill switches between *Individual items*
   (default) and *Table* (today's `DataTable`, Columns chooser included). This follows AGENTS.md
   ("enhance incrementally") and keeps a dense view for triage at volume.
7. **Gantt spans are derived, not invented.** Each request message (`EventRequest`,
   `RetryRequest`, `ResubmissionRequest`, `HandoffCompletedRequest`) is paired with the next
   response on the same event. The span runs from request enqueue to response enqueue.
   Unpaired messages and audits render as point markers. The "Timing (last attempt)" bar uses
   `Event.queueTimeMs` and `processingTimeMs`.
8. **"Resubmit with changes…" opens Event Details in v1.** The edit modal lives inside the
   1,000+ line `MessageListing`. Extracting it belongs with plan 03 (decompose WebApp event
   components). Resubmit and Skip work in the panel through the page's existing `act()`.
9. **Theme tokens only.** Colours come from the existing CSS variables and `STATUS_COLORS` /
   `ENDPOINT_PALETTE`. Dark mode works without new tokens. The mockup's hex values are the
   light-theme values of those tokens.

## Pull requests

Three PRs, each shippable on its own. PR 2 and PR 3 depend on PR 1's function changes only.

### PR 1: header, pills, search, chart with brush, legend tiles

- **`functions/failed-messages.functions.ts`**
  - Add `windowStart`/`windowEnd` to `FailedFilterValues` and `EMPTY_FAILED_FILTER`.
  - Add `selectedWindow(values)`, which prefers the window params and falls back to `bucket`.
    It replaces `selectedBucketWindow` at its three call sites.
  - Add `parseSearchQuery(text): Partial<FailedFilterValues>` and its inverse
    `searchQueryOf(values)`.
- **New `components/failed-messages/failed-filter-pills.tsx`.** It holds the pill row, with
  each pill opening a `DropdownMenu`:
  - time: the `PERIOD_OPTIONS` presets
  - view as: items or table
  - status: checkboxes with counts
  - endpoint and event type: `Combobox multiple`, reusing `FailedFilterBar`'s loaders
  - (+): From, To and "Reported" filters

  The search box sits below the pills. `FailedFilterBar` is deleted once nothing imports it.
- **`components/failed-messages/failed-histogram.tsx`**
  - Add the recharts `Brush` bound to `windowStart`/`windowEnd`, with a reset button.
  - The headline ("N unresolved failures between A and B") and the Group by select move into
    the chart card.
- **New `components/failed-messages/failed-legend.tsx`.** It renders the legend tiles as
  `<button aria-pressed>`, and toggling one edits `status`. "Endpoints affected" is text, not a
  toggle, and its peak text is the current `peak` logic.
- **`pages/failed-messages.tsx`.** Swap in the above. Keep the "older failures outside this
  range" hint under the headline.
- **Tests (vitest, written first):**
  - `failed-messages.functions.test.ts` (new): `parseSearchQuery` and `searchQueryOf` round
    trips, GUID detection, prefixes, and the `bucket` → window fallback.
  - `pages/failed-messages.test.tsx`: update "narrows the list, but not the chart, to a
    selected bar" (line 164) to assert `updatedAtFrom/To` from the window params, and add a
    brush case.

### PR 2: individual items list

- **New `components/failed-messages/failed-item-list.tsx`.** One row per failure:
  - status colour bar, `updatedAt - STATUS` link, blocked and resubmitted badges
  - monospace `errorTextOf(e)`
  - event type, endpoint, session and event ID
  - Resubmit, Skip and ⋯ actions

  The list takes `events`, `blocked`, `selected`, `onSelect`, `onAct`, `onOpen`,
  `hasMore` and `onLoadMore`, so `fetchPage`, `loadBlocked` and `act` stay in the page.
- **Bulk bar.** "Select all N", "k selected", Resubmit k and Skip k call the existing
  `act()`.
- **`pages/failed-messages.tsx`.** Render the item list or `DataTable` from the view-as pill.
  "Hide reported" applies to both.
- **Tests:** `failed-item-list.test.tsx` (new) covers:
  - rendering the error text and badges
  - selecting and running bulk Resubmit
  - Load more calling `onLoadMore` only when `hasMore`

  The existing "resubmits a row and removes it from the list" test (line 216) runs against both
  views.

### PR 3: transaction details panel

- **New `components/ui/side-panel.tsx`.** A right-anchored sheet built like `Modal`: portal,
  Escape, overlay click, scroll lock. It also traps focus and returns it to the opening row.
- **New `functions/flow-gantt.functions.ts`.** `buildFlowRows(messages, audits)` returns rows
  with `offsetMs`, `durationMs | undefined` and `kind`, following Decision 7.
- **New `components/failed-messages/flow-gantt.tsx`.**
  - It renders the rows against a time axis with ticks at round intervals, a duration column
    and a legend.
  - Selecting a row drives the detail section.
  - Move `MESSAGE_COLORS` from `flow-timeline.tsx` into `functions/message-type.functions.ts`
    so both components share it.
- **New `components/failed-messages/failure-detail-panel.tsx`.** It fetches the four calls
  listed under *Constraints* and renders:
  - a header with status, event ID, endpoint, "k of N", previous and next, *Open full page*
    and close
  - an action bar with Resubmit, "Resubmit with changes…" (links to Event Details) and Skip,
    plus the blocked, resubmits and retries chips
  - the gantt
  - the exception (`errorContent.errorText` and `exceptionStackTrace`) with `TimingBar`
  - properties: event, last message, session and correlation IDs, event type,
    from/originatingFrom, to, endpoint role, status, retries, resubmits, first enqueued,
    last updated and reported
  - `IntelligenceCard`, with a "Show them in Group by error" link that sets
    `view=error, endpointId=[this]`
  - blocked in session: the first five from `getEventBlockedId`, linking to each event
  - the payload (`messageContent.eventContent`), with PII redaction exactly as Event Details
    applies it (verify which fields `getEventId` redacts before the PR)
- **`pages/failed-messages.tsx`.**
  - Item titles and table rows open the panel through `?open=` (Decision 5) instead of
    navigating.
  - Previous and next step through the loaded `events`. At the last row, next calls
    `fetchPage(continuationToken, true)` first.
  - A resubmit or skip from the panel moves to the next failure.
- **Tests:**
  - `flow-gantt.functions.test.ts` (new): pairing, unpaired markers, audits interleaved by
    time, and retry numbering.
  - `failure-detail-panel.test.tsx` (new): it loads by `?open=`, Escape clears the param,
    Resubmit calls `postResubmitEventIds`, and it degrades when history is empty.

## Verification (each PR)

```bash
npm --prefix src/NimBus.WebApp/ClientApp run lint
npm --prefix src/NimBus.WebApp/ClientApp run test:ci
npm --prefix src/NimBus.WebApp/ClientApp run build
dotnet build src/NimBus.sln -c Release
```

- Run the page against the local Aspire stack (`dotnet run --project src/NimBus.AppHost`) with
  the simulator producing failures. Check light and dark themes and 1h / 7d / 30d ranges.
- Check a deep link with `?open=`, and a shared link carrying the legacy `bucket=`.
- Each PR description carries a screenshot of the finished UI with demo data (AGENTS.md).

## Risks

- **Brush and paging.** Dragging the brush refetches the list each time. Debounce URL writes by
  300 ms and cancel stale fetches with the existing `ticket` ref.
- **Old sessionStorage snapshots** (`nimbus.failed.filters`) lack the window params. The
  defaults cover them, so this needs a test, not a migration.
- **Long histories.** A failure retried and resubmitted many times can have dozens of messages.
  Cap the gantt at 50 rows with "Show all in Event Details".
- **Previous and next across pages** depends on continuation paging. At the very end, next is
  disabled rather than wrapping.

## Follow-ups (out of scope)

- A server-side `query` OR search and a `sort` field on `FailedSearchFilter`. Both need all
  three providers and conformance tests.
- Resubmit with changes inside the panel, after plan 03 extracts the modal.
- Using the same panel on the Messages and Endpoint Details lists.
