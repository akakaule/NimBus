# Talk track: Dynamics 365 Sales ↔ Business Central on NimBus

About **30 minutes** of demo plus Q&A. The audience: an IT lead and the people who will own the CRM
and ERP integration. The story: *CRM owns leads, prospects, opportunities and the pipeline; Business
Central owns the buying customer, contacts, products and quotes, and quotes are made in Business
Central, linked to the CRM opportunity.* The demo proves that NimBus keeps that boundary sharp, and
shows how an integration behaves when something goes wrong, which is what IT really buys.

Everything on screen is simulated and fictional ("Contoso Subsea"). Say so up front: *"The two
systems are simulators we control, so we can break them on purpose. The integration APIs have the
same shapes as the real Dataverse and Business Central APIs."* Then tell the client's own story over
the fictional data.

Record numbers such as `S-QUO1003` or `C00050` depend on what you did earlier in the run. Read them
off the screen.

## Before the meeting (15 minutes)

1. **Start the stack about 10 minutes early.** From the repo root, run
   `aspire run --apphost samples/DynamicsBcDemo/DynamicsBcDemo.AppHost/DynamicsBcDemo.AppHost.csproj`.
   Wait until every resource in the Aspire dashboard (https://localhost:17180) is healthy and
   `provisioner` is *Finished*.
   - For a client-facing run, prefer a real Service Bus namespace (see the [README](../README.md#using-a-real-service-bus-namespace)).
   - Restarting the stack is the reset: it starts from go-live morning every time.
2. **Warm-up cycle.** This touches only the throwaway *Wingtip Marine (warm-up)* records:
   - In **Sales Hub** (http://localhost:5283) → **Accounts** → *Wingtip Marine (warm-up)* → **Check
     credit in Business Central**. The first check after a start can take several seconds while the
     request/reply path warms up. Close the dialog and check again: now it answers in under a second.
   - Open the opportunity *Warm-up opportunity* (OPP-10099), change **Probability** and **Save**.
   - In **Business Central** (http://localhost:5293) → **CRM Opportunities** → *OPP-10099* → **Create
     sales quote** → **Edit lines** → add one item → **Save**. Sales Hub's OPP-10099 now shows the
     Business Central quote.
   - Don't run the initial sync: that is scene 1.
3. **In nimbus-ops** (https://localhost:18543), open **Admin → Health** and switch on the heartbeat
   schedule, so both adapters show as alive.
4. **Shared screen: open these tabs, in this order:**
   1. Sales Hub, **Accounts**;
   2. Business Central, **Home**;
   3. nimbus-ops, **Endpoints**;
   4. **#integration-alerts** (http://localhost:5293/demo/alerts).
5. **Presenter screen:** the **demo cockpit** (http://localhost:5293/demo) and this script.
6. In Sales Hub, set **Signed in as** to *Alex Rivera*.

## Scene 0 — Setting the scene (2 min)

*Show:* the ownership diagram in the [README](../README.md#the-ownership-model), or your own slide.

*Say:*
- "Today your sellers keep leads and pipeline in spreadsheets, and Business Central already runs the
  business. We keep that split: CRM owns leads, prospects, opportunities and the pipeline; Business
  Central owns the buying customer, contacts, products and quotes."
- "Quotes are made in Business Central, and linked to the CRM opportunity. From an account's first
  quote, Business Central manages it."
- "The integration's job isn't to copy everything everywhere. It's to move each piece of data at the
  right moment, in the right order, and to show you exactly what happened when something goes wrong."

## Scene 1 — Go-live: the initial sync (3 min)

*Click:*
1. Sales Hub → **Accounts**. Only a prospect and the warm-up account: none of Business Central's
   customers yet.
2. Cockpit → **Go-live** → **Run the initial sync**. It reports the product groups, customers and
   contacts it sent.
3. Sales Hub → **Accounts**: *Fabrikam Offshore Energy*, *Northwind Ocean Survey*, *Litware
   Renewables* and *Adatum Hydrographic* appear as **Customers**, locked for Business Central. Open
   *Fabrikam*: the **Business Central** section shows the customer number, credit limit and payment
   terms, and **Contacts** lists Ingrid Solberg and Erik Nilsen.
4. nimbus-ops → **Endpoints** → **D365SalesEndpoint**: the load — `BcItemCategoryUpdated`,
   `BcCustomerUpdated`, `BcContactUpdated` — all **Completed**.

*Say:*
- "Go-live day. The initial load runs through the same pipeline as everything else: every record is
  audited, and a customer's contacts always arrive after the customer."
- "Run it twice and nothing changes: CRM matches the records on their Business Central ids."
- "Items stay in Business Central. CRM gets the product groups to classify its opportunities."

## Scene 2 — A lead becomes an opportunity, and Business Central knows it (3 min)

*Click:*
1. Sales Hub → **Leads** → *ROV winch upgrade for research vessel* (Tailspin Marine Research, Hannah
   Okafor) → **Qualify**. You land on the new opportunity *OPP-10025*.
2. On the **Summary** tab, set **Product group** to *Winches and launch & recovery* → **Save**.
3. Point to the account: relationship type **Prospect**, managed in Dynamics 365.
4. Business Central → **Contacts**: *Tailspin Marine Research*, a **Prospect**, with the CRM account
   id. Then **CRM Opportunities**: *OPP-10025* with salesperson **AR** (matched on the seller's
   e-mail) and the product group.
5. Sales Hub → **Integration trail → Changes sent to Business Central** (it opens nimbus-ops):
   `D365ProspectUpdated`, then `D365OpportunityUpdated`, both **Completed**, in that order.

*Say:*
- "Qualifying creates the account, the contact and the opportunity. CRM owns them, and sends the
  prospect and the opportunity to Business Central straight away, in that order, so a quote can be
  made there."
- "Every message about Tailspin travels in Tailspin's own lane, in order. Here is the audit trail
  for exactly this customer."

## Scene 3 — The quote is made in Business Central (4 min)

*Click:*
1. Business Central → **CRM Opportunities** → *OPP-10025* → **Create sales quote**. The new quote is
   for the *contact* Tailspin Marine Research (not a customer), with salesperson **AR** and External
   Document No. **OPP-10025**.
2. **Lines** → **Edit lines** → **New line**: *Electric ROV winch, 20 kN* × 1; **New line**:
   *Armoured tow cable, per 100 m* × 3. Give the winch a 5 % line discount → **Save**.
3. **Send**.
4. Sales Hub → *OPP-10025*: the strip shows **Business Central quote … · Sent**. The **Quotes
   (Business Central)** tab has a read-only copy, and the **Timeline** shows the quote being created,
   revised and sent.
5. Open the account: a banner says **Business Central manages this account's master data since its
   first quote**. The fields are locked.

*Say:*
- "Quotes are made in Business Central, by the people who own pricing and delivery. They link the
  quote to the CRM opportunity with one click."
- "From the first quote, Business Central manages the account, and CRM shows it read-only. That's
  your ownership rule, enforced by the integration."
- "The opportunity stays the seller's. The quote status comes back to it."

## Scene 4 — Won: the prospect becomes a buying customer (3 min)

*Click:*
1. On the BC quote card → **Make Order**. The dialog explains that the contact isn't a customer yet
   and will be created from a customer template. Pick a template → **Confirm**.
2. In Sales Hub, open the account. It is now a **Customer**; the **Business Central** section shows
   the customer number, credit limit and payment terms from the template.
3. Open *OPP-10025*. It is **Won** from the accepted Business Central quote, with the quote total as
   actual revenue.
4. nimbus-ops → **Updates from Business Central** for this customer: `BcCustomerCreated` →
   `BcSalesQuoteUpdated` (Accepted), both **Completed**, in that order.

*Say:*
- "This is the handover you described. The prospect becomes a buying customer in Business Central.
  CRM learns about the customer first, then that the quote was accepted, and closes the deal as won —
  in that order, guaranteed per customer, without anyone writing locking code."
- "The order stays in Business Central. CRM doesn't need the order history."

## Scene 5 — Optional, a V2 idea: ask, don't copy (2 min)

*Click:*
1. Sales Hub → **Accounts** → *Fabrikam Offshore Energy* → **Check credit in Business Central**. The
   answer (credit limit, balance, available credit) arrives in well under a second.
2. Business Central → **Customers** → *Fabrikam* → set **Blocked** to *Ship* and raise the credit
   limit → **Save**.
3. In Sales Hub the account updates within seconds (credit on hold, timeline entry). Check credit
   again: **Blocked: Ship**.

*Say:* "Two patterns. A question — can this customer buy right now? — is asked live, request/reply.
A change — Business Central blocked shipping — is pushed as an event. The seller never sees stale
credit data."

## Scene 6 — When things go wrong (8 min)

The part that matters most to IT. Bring the **#integration-alerts** tab up next to nimbus-ops.

### 6a — Missing reference data: a new seller isn't in Business Central

*Click:*
1. Sales Hub → **Signed in as** → *Robin Hale* (a new seller). **Leads** → *Connectors for offshore
   wind export cable* (City Power & Light) → **Qualify**.
2. **#integration-alerts** shows *Message failed: D365OpportunityUpdated*.
3. nimbus-ops → **Failed**. The error is readable: *No salesperson with e-mail
   robin.hale@contososubsea.example exists in Business Central. Add the seller under Salespeople in
   Business Central, then resubmit the message.* The prospect reached Business Central; the
   opportunity did not.
4. While it is failed: cockpit → **Pilot sales office** → **Create opportunities now** (6). Six
   sellers' new prospects and opportunities flow through untouched (watch nimbus-ops **Flow** or
   **Endpoints**).
5. Fix it where the data lives: Business Central → **Salespeople** → **New**: code *RH*, *Robin
   Hale*, *robin.hale@contososubsea.example*.
6. nimbus-ops → the failed message → **Resubmit**. It completes, and *OPP-10029* appears in Business
   Central's **CRM Opportunities**, ready to be quoted.

*Say:* "A data problem stops one customer, not the office. Nothing is lost or silently retried. IT
gets an alert with the exact reason, fixes the data in Business Central, and resubmits. The history
shows who did what."

### 6b — Business Central update window

*Click:*
1. Cockpit → **Start 20 s update window**. The BC client shows a banner.
2. Cockpit → **Create opportunities now** (6).
3. Within seconds, the cockpit's circuit turns **Open** and **#integration-alerts** shows *Circuit
   opened*. The adapter has **paused**: the remaining messages wait on the bus, untouched.
4. About 20–40 seconds later the circuit goes **HalfOpen → Closed** (*Circuit recovered*). The
   messages that failed during the window are retried automatically, and all six opportunities
   appear in Business Central. No one clicked anything.

*Say:*
- "Business Central online has update windows and service limits. Instead of burning every message
  into failures, NimBus notices the outage, pauses, probes, and resumes on its own."
- "Every retry is in the audit trail."

### 6c — Throttling

*Click:*
1. Cockpit → **Start 20 s throttling**.
2. **Signed in as** *Maya Lindqvist* → *OPP-10017* (Proseware Cable Systems) → change **Est.
   revenue** → **Save**.
3. nimbus-ops shows the change failing with *429 Too Many Requests* and retrying with growing delays
   (about 5, 10 and 20 s). It completes by itself within about 40 seconds, on the first retry after
   the window, and Business Central's **CRM Opportunities** shows the new value. The circuit stays
   **Closed**: throttling is paced, not an outage.

*Say:* "Business Central limits requests per user. Backoff is the right answer to that, and here
it is policy, not code in every integration."

### 6d — The same change, twice

*Click:*
1. Sales Hub, still signed in as *Maya Lindqvist* → *OPP-10017* → change **Est. revenue** again →
   **Save**. Business Central's **CRM Opportunities** shows the new value within a second.
2. Cockpit → **Delivery** → **Deliver the last opportunity change again**.
3. nimbus-ops → **Endpoints** → **BusinessCentralEndpoint**: the repeat is **Skipped** with reason
   **DuplicateDetected**. Business Central applied the change once.

*Say:* "Source systems deliver at least once: a retry can send the same change twice. Every change
carries a fingerprint, so the platform recognises the repeat. The Business Central side is
idempotent as well: belt and braces."

## Scene 7 — Operating it (3 min)

Tour nimbus-ops:
- **Endpoints**: health per system.
- **Flow**: live traffic.
- **Event types**: the integration contract, with descriptions and examples, generated from code.
- **Personal data masking**: open any `D365ProspectUpdated` message. The contact person's name,
  e-mail and phone show as `***`, and the VAT number only reveals its last four characters, unless
  the user holds the PII Reader role.
- **Failed**: bulk resubmit or skip.
- **Heartbeat**: both adapters alive.
- **Monitor**: the wall display.
- **Access**: Entra ID sign-in with Reader, Contributor and Owner roles.

*Optional:*
- If an MCP client is configured, ask the read-only operator endpoint (`/mcp`): *"Which opportunity
  changes failed today, and why?"*
- Mention `nb catalog export`: the same contract as a browsable EventCatalog site.

## Scene 8 — From demo to production, and the pilot (3 min)

*Say, using the [README table](../README.md#from-demo-to-production):*
- **Dynamics 365 → NimBus.** A Dataverse Service Endpoint and an async plug-in step put changes on
  Service Bus. The NimBus Dataverse adapter (preview) turns them into the same messages you saw.
  Changes made by the integration user are filtered out, so nothing echoes back.
- **Business Central → NimBus.** BC webhooks notify about a change (about 30 s after it, and
  subscriptions must be renewed every 3 days). The integration fetches the record through the
  standard API. A small **AL extension** adds the CRM reference fields, the CRM opportunities table
  the quotes link to, and the API for prospects and opportunities.
- **Hosting.** Everything runs in your Azure tenant: Service Bus, adapters on Container Apps or
  Functions, the operator console, and a SQL or Cosmos audit store. It is deployed and upgraded with
  the `nb` CLI.
- **The pilot.** Go live with one sales office: its sellers are mapped to Business Central
  salespeople, and its Business Central customers, contacts and product groups are loaded into CRM
  with the initial sync you saw. Other offices and Business Central environments follow the same
  way, without changing the integration. The retry timings you saw are stage-tuned seconds;
  production uses minutes.

## Objection handling

**"Why not Microsoft's standard Business Central – Dynamics 365 Sales integration?"**
- Be fair: it is a solid option for standard couplings. It maps customers to accounts (by default,
  accounts whose relationship type is *Customer*), plus contacts, salespeople and currencies.
- It synchronises from scheduled job-queue entries, and Microsoft's own documentation says it
  "doesn't guarantee real time data consistency"
  ([source](https://learn.microsoft.com/en-us/dynamics365/business-central/admin-synchronizing-business-central-and-sales)).
- NimBus earns its place when you need:
  - near-real-time flow;
  - ownership rules the standard mapping doesn't model, such as quotes made in Business Central for
    prospects that aren't customers yet, linked to CRM opportunities;
  - per-message audit with resubmit and skip;
  - per-customer ordering;
  - resilience against throttling and update windows;
  - more systems than CRM and ERP later.
- The two can coexist: keep the standard coupling where it fits.

**"Why not Power Automate?"**
- It is great for simple, low-volume flows and notifications.
- Business Central's own documentation notes that its Power Automate connector can't process
  `collection` notifications, so a flow simply doesn't trigger when more than 1,000 records change
  within 30 seconds
  ([source](https://learn.microsoft.com/en-us/dynamics365/business-central/dev-itpro/api-reference/v2.0/dynamics-subscriptions)).
- Ordering, controlled retries, replay and a cross-system audit trail are yours to build there. In
  NimBus they are the platform.

**"What will it cost to run?"**
- The components are consumption-based: Service Bus Standard, Container Apps or Functions, a small
  SQL database or Cosmos.
- A pilot office produces tens to hundreds of messages a day. Size it with the Azure pricing
  calculator.
- NimBus itself is open source (MIT): no per-message or per-user licence.

**"What happens if Business Central is down for hours?"**
- Messages wait durably on Service Bus. The adapter pauses, probes, and resumes when BC is back.
  Nothing is lost, and every attempt is audited (scene 6b).
- What stops is only what depends on BC, and only for the customers affected.

**"Who operates it, and how would we know something is wrong?"**
- Alerts go to a Teams channel (scene 6).
- nimbus-ops shows every message per customer, with roles for read-only users, operators and
  owners.
- Most incidents are data problems: fix in the owning system, then resubmit.

**"How do we change the integration safely?"**
- The contracts are versioned code, and the platform validates them when it deploys.
- The catalog is exportable as AsyncAPI or EventCatalog for review.

**"Can we see everything that happened to one customer?"**
- Yes. The integration trail links on the CRM forms open that customer's session in nimbus-ops,
  and the CRM timeline shows what the integration changed.

## If something misbehaves live

- **A scene doesn't react within ~10 s.** Check the Aspire dashboard: are the adapters running? The
  web clients poll every few seconds; refresh the page.
- **The initial sync seems to do nothing.** Check nimbus-ops → **Endpoints** →
  **D365SalesEndpoint**. Running it again is safe.
- **Leftovers from a rehearsal** (a failed message, an open circuit). Restart the AppHost: it resets
  everything in about 2 minutes. With a real namespace, purge the demo subscriptions first.
- **6d processes the repeat instead of skipping it.** Redeliver a change that went through on its
  first attempt: one that only succeeded after a retry or a resubmit (such as the throttled change in
  6c) is handled again. That is why 6d starts with a fresh change.
- **Circuit still Open after the window.** It closes on the next successful probe. Retries land
  about 30 s after the failures. Keep talking through 6b's "nothing is lost" point.

## Cheat sheet (seed data)

| Record | Use |
|---|---|
| Cockpit → **Run the initial sync** | Scene 1 |
| Lead *ROV winch upgrade for research vessel* (Tailspin Marine Research), owner *Alex Rivera* | Scenes 2–4 |
| *Fabrikam Offshore Energy* (C00010), in CRM after the initial sync | Scene 5 (optional) |
| Lead *Connectors for offshore wind export cable* (City Power & Light), owner *Robin Hale* (no BC salesperson) | Scene 6a |
| *OPP-10017* Proseware Cable Systems, owner *Maya Lindqvist* | Scene 6c |
| *S-QUO1001*, Litware Renewables | Spare: a quote Business Central made on its own; CRM never hears of it |
| *Wingtip Marine (warm-up)* and *OPP-10099* | Warm-up only |
