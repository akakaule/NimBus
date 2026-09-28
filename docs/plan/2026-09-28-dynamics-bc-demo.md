# Dynamics 365 Sales ↔ Business Central demo (`samples/DynamicsBcDemo`)

Status: in progress (2026-09-28) on branch `claude/nimbus-dynamics-bc-demo-4918f0`.

## Context

The demo targets an organisation that is implementing Dynamics 365 Sales, already runs Business
Central, and wants to see NimBus as the integration platform between the two. The repo is public,
so the sample uses a fictional company. The ownership model it demonstrates:

- **CRM (D365 Sales) owns** leads, opportunities and the pipeline — everything until the prospect
  becomes a buying customer.
- **BC owns** the buying customer (the customer master data).
- **Quotes are made in BC**, and become orders there.
- Rollout starts with one pilot sales office.

Decisions taken with the user (2026-09-28):

1. **A new, focused sample.** `CrmErpDemo` stays untouched. It creates an ERP customer for every
   CRM account, which contradicts "BC owns only buying customers", and its e2e suite, CI gate and
   film depend on that round-trip.
2. **Simulated systems with real API shapes.** Look-alike D365 Sales and BC apps whose integration
   APIs follow the Dataverse Web API and BC API v2.0 names and shapes. The demo runs offline with
   no tenants or credentials.
3. **Public repo, fictional company.** "Contoso Subsea" is a fictional maker of subsea equipment.
   No client name, people, pilot office or dates go into git.
4. **Deliverables:** the runnable demo and a presenter talk track. No Playwright suite, CI
   workflow, film or Azure deployment.

Outcome: `aspire run` brings up a D365 look-alike, a BC look-alike and nimbus-ops, plus a talk
track of about 30 minutes that goes from lead to won order and then through failure handling.

## Verified external facts the design relies on

- **BC can quote a prospect contact.** BC can make a sales quote for a *contact* that isn't a
  customer yet. **Make Order** then creates the customer from the contact via a customer template.
  That matches "BC owns the buying customer".
  ([MS Learn](https://learn.microsoft.com/en-us/dynamics365/business-central/sales-how-make-offers),
  [Olof Simren](https://www.olofsimren.com/sales-quote-without-customer/))
- **The standard BC API v2.0 `salesQuote` is customer-based.** It has no sell-to-contact field;
  `status` is `Draft|Sent|Accepted|Expired`; it has `externalDocumentNumber`, `salesperson` and
  `validUntilDate`, and the actions `send`, `makeOrder` and `makeInvoice`. A real implementation
  therefore needs a small **AL extension**: CRM reference fields plus a custom API
  (`api/contoso/crm/v1.0/...`) for prospect quotes. The simulator exposes exactly that custom API.
- **BC API limits.** Per user: 6,000 OData requests per 5 minutes, then `429`. After 5 concurrent
  requests, calls queue and return `503` after 8 minutes; update windows also return `503`.
  ([operational limits](https://learn.microsoft.com/en-us/dynamics365/business-central/dev-itpro/administration/operational-limits-online))
- **BC webhooks** carry only `{resource, changeType}`, arrive about 30 s after the change, and
  expire after 3 days (renewal needs a handshake). If the endpoint answers with anything other than
  408/429/5xx, BC deletes the subscription. The Power Automate BC connector can't process
  `collection` notifications (more than 1,000 changes in 30 s).
  ([webhooks](https://learn.microsoft.com/en-us/dynamics365/business-central/dev-itpro/api-reference/v2.0/dynamics-subscriptions))
- **The standard BC↔Dataverse coupling** runs on job-queue schedules and "doesn't guarantee real
  time data consistency". By default it maps Customer↔Account, filtered on Relationship Type =
  Customer.
  ([sync](https://learn.microsoft.com/en-us/dynamics365/business-central/admin-synchronizing-business-central-and-sales))
- **Dataverse account:** `customertypecode` (3 = Customer, 8 = Prospect), `accountnumber`,
  `creditlimit`, `creditonhold`. Opportunities are won with `POST WinOpportunity`
  `{Status: 3, OpportunityClose: {"opportunityid@odata.bind": ...}}`.

## Story and proof points (the MVP is scenes 1–7d + scene 8)

| # | Step (fictional) | Proves |
|---|---|---|
| 1 | Lead "Tailspin Marine Research" qualified → account (**Prospect**), contact, opportunity; nothing reaches BC | Ownership boundary |
| 2 | Seller adds lines → **Request quote in BC** | **Command** `CreateBcSalesQuote` (one consumer, validated at provisioning), session per account |
| 3 | BC creates a prospect **contact** (not a customer) + **draft quote**; the opportunity shows it | Events, transactional **outbox**, Dataverse-shaped writes |
| 4 | BC **Send** → estimated revenue follows the real quote | Event-driven pipeline |
| 5 | BC **Make Order** → customer + order → D365 account flips to Customer with locked BC fields; opportunity **Won** | **Session ordering** (customer → quote accepted → order) |
| 6 | **Check credit in BC**; BC changes credit limit/blocked → D365 mirror updates | **Request/reply** vs events |
| 7a | Seller missing in BC → **Failed**, only that session blocks while a burst from other accounts flows; add salesperson in BC → **Resubmit** | Isolation, operator recovery, alert |
| 7b | 20 s BC update window (503) → retries scheduled, circuit **opens**, waits, probes → recovers with no operator action | **Circuit breaker** + retry policy, alerts |
| 7c | 20 s throttling (429) → backoff, completes by itself | **Retry policy** (broker-scheduled) |
| 7d | Double-click Request quote → **DuplicateDetected**, one BC quote | Inbox + deterministic MessageId |
| 8 | nimbus-ops tour, demo → production, pilot rollout (spoken) | Operator UI, catalog, Azure hosting |

## Integration design

**Endpoints.** `DynamicsBcPlatformConfiguration : Platform` declares two:

- `D365SalesEndpoint` (SystemId `D365Sales`)
- `BusinessCentralEndpoint` (SystemId `BusinessCentral`)

Messages live in the namespaces `…Contracts.D365Sales` and `…Contracts.BusinessCentral`, because
the Event Types page groups by namespace. Event names are `D365*` / `Bc*` and the command is
`CreateBcSalesQuote`. EventTypeId is global to a Service Bus namespace, so a test guards against
collisions with the other samples.

**Messages.** All have `[Description]`, `static Example` and `[Sensitive]` on PII. The events and
the command carry `[SessionKey(nameof(AccountId))]` and an `AccountId` property.

| Message | Kind | Direction | Notes |
|---|---|---|---|
| `CreateBcSalesQuote` | Command | D365 → BC | Opportunity id/no, prospect snapshot, BcCustomerNumber?, primary contact, seller e-mail, lines, currency. Published with MessageId `quote:{opportunityId}:{rev}` (`IPublisherClient.cs:23`) so the BC adapter's inbox dedups a double-click |
| `D365ProspectUpdated` | Event | D365 → BC | Only for prospects already known to BC; BC answers 409 "owned by BC" for converted customers → the adapter logs and completes (ownership rule) |
| `D365CreditCheckRequested` | Request | D365 → BC | **No session key.** The handler **never throws**: it returns `BcCreditStatus{Status: Ok/NotFound/Unavailable, ...}`, because `RequestJsonHandler.cs:77-81` rethrows and would fail/block the request otherwise |
| `BcSalesQuoteCreated` / `…Updated` | Event | BC → D365 | QuoteId/No, OpportunityId, ExternalDocumentNumber, Status, totals, ValidUntil, SentDate, SellToContactNumber |
| `BcCustomerCreated` / `…Updated` | Event | BC → D365 | CustomerId/No, CrmAccountId? (null for BC-native → session falls back to CustomerId), address, CreditLimit, BalanceDue, Blocked, PaymentTerms, Origin |
| `BcSalesOrderCreated` | Event | BC → D365 | OrderNo, QuoteNo, OpportunityId, CustomerId/No, totals |

**Make Order** raises customer created → quote accepted → order created with three sequential
`Publish` calls in one outbox transaction (not a batch): the outbox orders by `CreatedAtUtc` with no tiebreaker (`SqlServerOutbox.cs:178`, flagged as a separate
task). The D365 handlers are order-tolerant anyway; the order handler also ensures the account
link.

**Real-shaped surfaces** (only what the adapters call):

- **BC API:**
  - `GET /api/v2.0/companies({cid:guid})/customers({id:guid})` (+ `customerFinancialDetails`)
  - custom `POST /api/contoso/crm/v1.0/companies({cid:guid})/quoteRequests` — one transaction:
    find-or-create the contact, resolve the salesperson by e-mail, check items, create a draft quote
    idempotently on `crmOpportunityId`, write to the outbox
  - `PATCH …/prospects({crmAccountId:guid})`
- **D365 API:**
  - `PATCH /api/data/v9.2/accounts({id:guid})` and the alternate key `accounts(cs_bccustomerid={id:guid})`
  - `cs_bcquotes(cs_bcquoteid={id:guid})` — keyed on BC's quote GUID
  - `PATCH opportunities({id:guid})`, `POST WinOpportunity`
  - Every route has `:guid` constraints to avoid ambiguous matches.
  - These writes add timeline entries and **never publish**; only `/api/app/*` (UI) writes publish.
    This prevents the echo loop, and matches real Dataverse, where integration-user changes must
    be filtered.
- The adapters use typed `IBusinessCentralClient` / `IDataverseClient`, so real endpoints later
  need auth + base URLs, not a redesign.

**Hosting and resilience.** Both adapters are **workers** with
`AddNimBusDeferredProcessorHostedService`. The Resolver still shows Functions hosting.

`BusinessCentral.Adapter`:

- **HTTP client:** `IBusinessCentralClient` gets its own `HttpClient` outside `IHttpClientFactory`
  (the per-client opt-out, `RemoveAllResilienceHandlers()`, is experimental: EXTEXP0001), because
  ServiceDefaults' standard handler (`NimBus.ServiceDefaults/Extensions.cs:26-29`) would hide
  retries and throw exceptions no rule matches. It keeps a plain timeout. It maps 429 →
  `BcThrottledException`, 503/408 → `BcUnavailableException` and other 4xx →
  `BcRequestRejectedException` (the error text names the BC page to fix). No exception name
  contains "Validation".
- **Retry policies,** via `ConfigureRetryPolicies` with rules matched on **type names**. The
  matched text includes stack traces (`StrictMessageHandler.cs:608`). Exponential backoff with
  bounded jitter:
  - Throttled: base 5 s, max 60 s, 5 retries.
  - Unavailable: base 30 s, so retries land after the 20 s window.
  - No default policy: a 4xx fails and blocks the session until an operator acts.
- **Circuit breaker:** `WithCircuitBreaker` with MinimumThroughput 3, 50 %, window 60 s, break
  10 s, 1 probe, `Exclude<BcRequestRejectedException>()` and `Exclude<BcThrottledException>()`
  (throttling is paced by retries; only outages count).
  - Failed retries currently count as breaker successes (`StrictMessageHandler.cs:205-212`). This
    is flagged as a separate task.
  - Stage timings must therefore make the probes fresh messages that land after the window.
- **Receivers:** `SessionIdleTimeout` 3 s (the 30 s default stalls probing); prefetch 0.
- **Inbox:** `UseInbox` with the SQL store in the **`nimbus` DB** (BC is SaaS, so never in BC's
  database).
- **Credit check:** `AddRequestHandler` for the credit-check responder.
- **Notifications:** `AddNimBusNotifications` + `AddWebhook` → `bc-api /api/demo/alerts`, with
  `WithDedupWindow(10 s)`. The circuit alert id is constant with a 5-minute default window
  (`NotificationLifecycleObserver.cs:149`).
- **Circuit reporting:** `CircuitStateReporter` → `bc-api /api/demo/circuit-state`.

Faults live **in the BC API**, never in adapter middleware: middleware exceptions dead-letter
immediately (`MessageHandler.cs:165-183`). The maintenance (503) and throttling (429) toggles
**expire on their own** (20 s each by default).

**Demo controls.** The look-alikes stay free of demo gadgets. A hidden **`/demo`** route in
BusinessCentral.Web (no nav entry; the Vite proxy reaches both APIs) holds:

- the 503/429 toggles with countdowns;
- the circuit state;
- **Burst** (`POST d365-api /api/demo/burst?n=` → N fresh prospect accounts from different sellers,
  each requesting a quote). This is deterministic, trips the breaker the same way every time, and
  shows isolation in 7a.
- nimbus-ops links.

**`/demo/alerts`** renders the webhook feed as a Teams-style channel on its own tab of the shared
screen, so the audience sees the alert.

**State is fresh every run.** The SQL container uses `ContainerLifetime.Session` with no data
volume, and the emulator is in memory, so reset means restart the AppHost. Broker session blocks
and scheduled retries survive any in-app reset, so there is none. Seed IDs are fixed
(`Contracts/Demo/SeedData.cs`), so `?sessionId=` links stay stable, and the D365 forms render an
"Open in nimbus-ops" link at runtime (`/Endpoints/Details/<endpoint>?sessionId=<accountId>`).
Number series use SQL `SEQUENCE`s.

**Seed data.**
- **Customers:** 4 existing BC customers, already linked in D365 (Fabrikam Offshore Energy,
  Northwind Ocean Survey, Litware Renewables, Adatum Hydrographic).
- **Prospects and leads:** Tailspin Marine Research, Trey Research Vessels, Proseware Cable
  Systems.
- **Items:** 8 generic subsea items (winch, A-frame LARS, wet-mate connector, fibre-optic rotary
  joint, tow cable, towed sensor platform, 4K camera, service agreement).
- **Sellers:** mapped to BC salespeople by e-mail, with one seller deliberately missing in BC. The
  D365 top bar has a "Signed in as" switcher.
- **Warm-up account:** one throwaway account that the talk track never opens.

## Projects

```
samples/DynamicsBcDemo/
  README.md, aspire.config.json, docs/talk-track.md
  DynamicsBcDemo.AppHost/        SQL (d365, bc, nimbus — nimbus always, for store + inbox), emulator default,
                                 resolver, nimbus-ops (+ NimBus__Mcp__EnableForLocalDevelopment), 2 APIs, 2 workers, 2 Vite apps
  DynamicsBcDemo.Contracts/      endpoints, messages, BcCreditStatus, platform, SeedData, Http helpers
  DynamicsBcDemo.Provisioner/    ServiceBusTopologyProvisioner.ApplyAsync
  D365Sales.Api/                 EF Core; /api/app/*; /api/data/v9.2/*; publisher; credit-check requester; /api/demo/burst
  D365Sales.Adapter/             worker; Bc* handlers → IDataverseClient
  D365Sales.Web/                 Sales Hub look-alike
  BusinessCentral.Api/           EF Core + outbox + dispatcher; /api/app/*; /api/v2.0 + /api/contoso/*; /api/demo/*
  BusinessCentral.Adapter/       worker; inbox, retries, breaker, request handler, notifications
  BusinessCentral.Web/           BC look-alike + hidden /demo, /demo/alerts
tests/DynamicsBcDemo.Tests/      MSTest
```

- **.NET projects** go into `src/NimBus.sln` (Samples folder). They copy CrmErpDemo's csproj
  opt-outs and package versions (Aspire 13.5.4, EF Core 10.0.11) and keep the AppHost's
  `MessagePack` pin, with a new `UserSecretsId`.
- **SPAs** use React 19 + Vite + TS + **Fluent UI React v9** (9.74.9 allows React <20). They are
  outside the sln, so the README covers `npm install`/`build`, and the lockfiles are committed and
  `npm audit`-clean; the daily audit finds them itself.
- **Branding:** no Microsoft logos. Titles read "Contoso Subsea · Sales Hub (simulated)" and
  "… · Business Central (simulated)", and the README carries a not-affiliated disclaimer.
- **Ports** (every pinned port shifted by 100 from CrmErpDemo's, so the two run side by side):

  | Resource | Port |
  |---|---|
  | d365-api | 5280 |
  | d365-web | 5283 |
  | bc-api | 5290 |
  | bc-web | 5293 |
  | nimbus-ops | 18180/18543 |
  | dashboard | 17180/15180 |
  | OTLP, resource service | shifted by 100 (19180/19190, 21180/23180) |

  Vite ports are pinned via `.WithEndpoint("http", e => e.Port = …)`, with no second endpoint.
  Cross-app URLs reach the SPAs as `VITE_*` environment variables. Re-check the ports against
  `netsh … excludedportrange` before pinning.
- **MVP screens:**
  - **D365:** the Leads list with qualify; the Opportunities list and form (BPF chevrons, product
    lines, Request quote, a read-only "Quotes (Business Central)" tab with BC deep links, Timeline
    with a nimbus-ops link); the Accounts list and form (Relationship-type badge, lock icons on
    BC-owned fields, Check credit).
  - **BC:** a role centre with cues; Sales Quotes list and card (edit lines, Send, Make Order with
    a customer-template dialog); Customers list and card (credit limit/blocked editing);
    Salespeople with an add form; Contacts (read-only, showing "CRM Account ID" — the proof that a
    prospect is not a customer); Sales Orders list.
  - **Deferred:** D365 dashboard and contacts, BC items, the blocked-item scene, AI failure
    analysis (TypeSafe provider, sends evidence off-tenant), bulk re-sync, DbGate.

## Reuse (copy and rename; no cross-sample references)

- **AppHost** wiring from `CrmErpDemo.AppHost/Program.cs`: the emulator switch, provisioner
  `WaitForCompletion`, Resolver, nimbus-ops with `NimBus__PlatformType`/`PlatformAssembly` and
  `EnableLocalDevAuthentication`, and `NimBus__Flow__WebAppUrl`.
- **Provisioner:** `CrmErpDemo.Provisioner/Program.cs:19-26`.
- **Outbox:** the wiring in `Erp.Api/Program.cs:40-61`, plus `OutboxScope` (single connection) and
  `SqlServerOutbox.EnsureTableExistsAsync`.
- **Worker subscriber:** `Crm.Adapter/Program.cs:57-137`, minus CloudEvents and PartnerInbound.
- **Helpers:** `ResponseAssertions` (as the basis of the typed client), `NoopPublisherClient`, the
  startup DB-init loop, `AlertsState`/`AlertsEndpoints`, `CircuitStateReporter`, and the
  mode-state singletons (with expiry added).
- **Requester error mapping:** `Crm.Api/Endpoints/ErpIntegrationEndpoints.cs`.

## Talk track (`docs/talk-track.md`, English, ~30 min)

- **Prep:**
  - Start about 10 minutes early.
  - For client-facing runs, prefer a real Service Bus namespace (`NIMBUS_SB_EMULATOR=false`), as
    the CrmErpDemo film README advises.
  - Run the warm-up cycle on the throwaway account.
  - Turn on the heartbeat schedule.
  - Tabs: D365, BC, nimbus-ops `/Flow`, `/demo/alerts`; `/demo` on the presenter screen.
- **Per scene:** clicks, what to say, the NimBus proof and the nimbus-ops link. The talk track
  explains that a retrying message shows Failed with a blocked session until its scheduled retry.
  Include live-recovery tips.
- **Demo → production:**
  - D365: Dataverse Service Endpoint → Service Bus → NimBus Dataverse adapter (preview,
    `feat/dataverse-adapter`) → mapping.
  - Loop prevention: filter integration-user changes.
  - BC: webhooks → ingress → API v2.0 fetch, plus the AL extension.
  - Hosting: Container Apps / Functions, Service Bus Standard, SQL store, `nb` deploy.
  - Pilot: one office, then an initial load of its customers through the same pipeline.
  - Production retry timings in minutes, not seconds.
- **Optional:** the read-only `/mcp` operator Q&A, and `nb catalog export` ("the contract,
  generated from code").
- **Objection handling (fair, sourced):** the standard BC↔Sales coupling, Power Automate,
  cost/volume at pilot scale, who operates it, adding more systems.

## Implementation order (one branch, Conventional Commits, one PR)

1. `docs(plan)`: this plan → `docs/plan/2026-09-28-dynamics-bc-demo.md`.
2. `feat(samples)`: Contracts + Provisioner + AppHost + sln. Check: topology provisions and
   nimbus-ops lists both endpoints.
3. `feat(samples)`: BusinessCentral.Api + BusinessCentral.Adapter.
4. `feat(samples)`: D365Sales.Api + D365Sales.Adapter. Check: the quote → order round trip works
   through `/api/app` calls.
5. `feat(samples)`: the D365 and BC look-alike SPAs, and the `/demo` pages.
6. `test(samples)`: `tests/DynamicsBcDemo.Tests`.
7. `docs(samples)`: README, talk track, screenshots (fictional data), and the list touch-ups in
   `AGENTS.md:56-57`, `README.md:26`, `docs/getting-started.md:12` and
   `docs/dependency-security.md:14`.

## Verification

- **Build and test:** `dotnet build src/NimBus.sln -c Release`, then
  `dotnet test tests/DynamicsBcDemo.Tests -c Release --no-build`.
- **Tests cover:**
  - catalog validation (`EnsureCommandConsumers`, session keys, prefixes, no EventTypeId collision
    with the CrmErpDemo or AspirePubSub catalogs);
  - the client mapping 429/503/4xx → typed exceptions, and the retry rules resolving to the right
    policy (and none for 4xx);
  - BC domain services (quote request idempotent per opportunity; Make Order converts the contact
    once and emits customer → quote accepted → order; a missing salesperson gives an actionable
    rejection);
  - the BC ownership rule (409 for a converted customer).
- **SPAs:** `npm ci && npm run build` in both.
- **Live:** `aspire run --apphost <abs path>`. Walk every scene in the browser pane and check each
  expected nimbus-ops outcome (Completed; Failed → Resubmitted → Completed; circuit Closed → Open →
  HalfOpen → Closed with no manual step; the 429 scene completing by itself; DuplicateDetected
  with one BC quote). Capture screenshots, then clean up any orphan SQL containers.
- **Report** which checks ran and anything skipped.

## Out of scope

- Real BC or D365 connectors.
- A Playwright suite/CI workflow, a film, Azure deployment.
- Opportunity lost → quote archive; item/price and invoice sync.
- A Danish talk track.

Out-of-scope issues found and flagged as separate tasks:

- the breaker counts failed retries as successes;
- the outbox order has no tiebreaker;
- about 14 doc/comment inaccuracies.
