# Talk track: Dynamics 365 Sales ↔ Business Central on NimBus

About **30 minutes** of demo plus Q&A. The audience: an IT lead and the people who will own the CRM
and ERP integration. The story: *CRM owns everything until the customer buys; Business Central
owns the buying customer, and quotes are made in Business Central.* The demo proves that NimBus
keeps that boundary sharp, and shows how an integration behaves when something goes wrong, which is
what IT really buys.

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
   - Restarting the stack is the reset: it starts from the seed data every time.
2. **Warm-up cycle.** This touches only throwaway data, never the scenes' records:
   - In **Sales Hub** (http://localhost:5283), open the opportunity *Warm-up opportunity*
     (OPP-10099, account *Wingtip Marine (warm-up)*) and click **Request quote in Business
     Central**. Within a few seconds it should show a Business Central quote.
   - On **Fabrikam Offshore Energy**, click **Check credit in Business Central**. It is read-only,
     so the scene 5 record stays as seeded. The first check after a start can take several seconds
     while the request/reply path warms up, which is why it happens here. Close the dialog and
     check again: the answer should now appear in under a second.
3. **In nimbus-ops** (https://localhost:18543), open **Admin → Health** and switch on the heartbeat
   schedule, so both adapters show as alive.
4. **Shared screen: open these tabs, in this order:**
   1. Sales Hub, the **Dashboard**;
   2. Business Central, **Home** (http://localhost:5293);
   3. nimbus-ops, **Endpoints**;
   4. **#integration-alerts** (http://localhost:5293/demo/alerts).
5. **Presenter screen:** the **demo cockpit** (http://localhost:5293/demo) and this script.
6. In Sales Hub, set **Signed in as** to *Alex Rivera*.

## Scene 0 — Setting the scene (2 min)

*Show:* the ownership diagram in the [README](../README.md#the-ownership-model), or your own slide.

*Say:*
- "Today your sellers keep leads and pipeline in spreadsheets, and Business Central already runs the
  business. We keep that split: CRM owns everything until the customer buys; Business Central owns
  the buying customer, and quotes are made in Business Central."
- "The integration's job isn't to copy everything everywhere. It's to move each piece of data at the
  right moment, in the right order, and to show you exactly what happened when something goes wrong."

## Scene 1 — A lead becomes an opportunity, in CRM only (3 min)

*Click:*
1. Sales Hub → **Leads** → *ROV winch upgrade for research vessel* (Tailspin Marine Research,
   Hannah Okafor).
2. **Qualify**. You land on the new opportunity *OPP-10025*.
3. Point to the account: its relationship type is **Prospect**. Open the **Timeline**.

*Say:* "Qualifying creates the account, the contact and the opportunity. Nothing has gone to
Business Central: a prospect is CRM's business until it buys."

*Prove it (optional):* open nimbus-ops **Endpoints**. There is no traffic for this account yet.

## Scene 2 — Ask Business Central for a quote (4 min)

*Click:*
1. On OPP-10025 → **Product lines** → **Edit**. Add *Electric ROV winch, 20 kN* × 1 and *Armoured
   tow cable, per 100 m* × 3 → **Save**.
2. **Request quote in Business Central**.
3. Within a few seconds the status strip shows a **Business Central quote … · Draft**. Estimated
   revenue now shows the quote total (€212,800) and the stage moves to **Propose**. Open the
   **Quotes (Business Central)** tab: a read-only copy.
4. Switch to **Business Central → Contacts**. The new contact is *Tailspin Marine Research*, marked
   **Prospect**, with the CRM account id.
5. Open **Sales Quotes** and the new quote. It is for the *contact*, it has salesperson **AR**
   (resolved from the seller's e-mail), and its External Document No. is **OPP-10025**.
6. Back in Sales Hub, click **Integration trail → Requests to Business Central** (it opens
   nimbus-ops). `CreateBcSalesQuote` is **Completed**. **Updates from Business Central** shows
   `BcSalesQuoteCreated` **Completed**.

*Say:*
- "Quotes are made in Business Central, so CRM *asks*: the message is a **command** with exactly
  one receiver, and the platform refuses to deploy if anyone adds a second one."
- "Business Central quotes the prospect as a *contact*. There is still no customer: we haven't sold
  anything yet."
- "Every message about Tailspin travels in Tailspin's own lane, in order. Here is the audit trail
  for exactly this customer."

## Scene 3 — Quote work happens in Business Central (2 min)

*Click:*
1. On the BC quote card → **Lines** → **Edit** → give the winch a 5 % line discount → **Save**.
2. **Send**.
3. In Sales Hub, the opportunity shows **Sent** and the revised estimated revenue. The **Timeline**
   shows the revision and the send.

*Say:* "Pricing, discounts and delivery belong to Business Central. The pipeline in CRM follows
the real quote automatically, so the sales manager's forecast uses Business Central's numbers, not
list prices."

## Scene 4 — Won: Business Central takes ownership (4 min)

*Click:*
1. On the BC quote card → **Make Order**. The dialog explains that the contact isn't a customer yet
   and will be created from a customer template. Pick a template → **Confirm**.
2. The message says the sales order and the customer were created.
3. In Sales Hub, open the account. It is now a **Customer**, and a banner says **Business Central
   owns this customer's master data**: the fields are locked. The **Business Central** section shows
   the customer number, credit limit and payment terms from the template.
4. Open the opportunity. It is **Won**, with the order number and the order amount as actual
   revenue.
5. Try to rename the account in Sales Hub. It is refused, because Business Central owns it.
6. nimbus-ops → **Updates from Business Central** for this customer: `BcCustomerCreated` →
   `BcSalesQuoteUpdated` (Accepted) → `BcSalesOrderCreated`, all Completed, in that order.

*Say:*
- "This is the handover moment you described. The prospect becomes a buying customer, and from now
  on Business Central is the master; CRM shows it read-only."
- "Order matters. The customer must exist before the order closes the opportunity, and NimBus
  guarantees that per customer, without anyone writing locking code."

## Scene 5 — Ask, don't copy: live credit status (2 min)

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
1. Sales Hub → **Signed in as** → *Robin Hale* (a new seller). Open **Opportunities** → *OPP-10016*
   (Trey Research Vessels) → **Request quote in Business Central**. The status stays *Quote
   requested…*.
2. **#integration-alerts** shows *Message failed: CreateBcSalesQuote*.
3. nimbus-ops → **Failed**. The error is readable: *No salesperson with e-mail
   robin.hale@contososubsea.example exists in Business Central. Add the seller under Salespeople in
   Business Central, then resubmit the message.*
4. While it is failed: cockpit → **Pilot sales office** → **Request quotes now** (6). Six sellers'
   requests flow through untouched (watch nimbus-ops **Flow** or **Endpoints**).
5. Fix it where the data lives: Business Central → **Salespeople** → **New**: code *RH*, *Robin
   Hale*, *robin.hale@contososubsea.example*.
6. nimbus-ops → the failed message → **Resubmit**. It completes, and the quote appears on the
   opportunity in Sales Hub.

*Say:* "A data problem stops one customer, not the office. Nothing is lost or silently retried. IT
gets an alert with the exact reason, fixes the data in Business Central, and resubmits. The history
shows who did what."

### 6b — Business Central update window

*Click:*
1. Cockpit → **Start 20 s update window**. The BC client shows a banner.
2. Cockpit → **Request quotes now** (6).
3. Within seconds, the cockpit's circuit turns **Open** and **#integration-alerts** shows *Circuit
   opened*. The adapter has **paused**: the remaining requests wait on the bus, untouched.
4. About 20–40 seconds later the circuit goes **HalfOpen → Closed** (*Circuit recovered*). The
   requests that failed during the window are retried automatically, and all six quotes are
   created. No one clicked anything.

*Say:*
- "Business Central online has update windows and service limits. Instead of burning every message
  into failures, NimBus notices the outage, pauses, probes, and resumes on its own."
- "Every retry is in the audit trail."

### 6c — Throttling

*Click:*
1. Cockpit → **Start 20 s throttling**.
2. **Signed in as** *Maya Lindqvist* → *OPP-10012* (Northwind Ocean Survey, an existing customer) →
   **Request quote in Business Central**.
3. nimbus-ops shows it failing with *429 Too Many Requests* and retrying with growing delays. It
   completes by itself in about 20 seconds. The circuit stays **Closed**: throttling is paced, not
   an outage.

*Say:* "Business Central limits requests per user. Backoff is the right answer to that, and here
it is policy, not code in every integration."

### 6d — The double-click

*Click:*
1. *OPP-10017* (Proseware Cable Systems) → click **Request quote in Business Central** twice,
   quickly.
2. nimbus-ops → **Requests to Business Central**: one request **Completed**, the second
   **Skipped** with reason **DuplicateDetected**. Business Central has exactly one quote.

*Say:* "The request carries a fingerprint, so the platform recognises a repeat. Business Central
is idempotent per opportunity as well: belt and braces against duplicate quotes."

## Scene 7 — Operating it (3 min)

Tour nimbus-ops:
- **Endpoints**: health per system.
- **Flow**: live traffic.
- **Event types**: the integration contract, with descriptions and examples, generated from code.
- **Personal data masking**: open any `CreateBcSalesQuote` message. The contact person's name,
  e-mail and phone show as `***`, and the VAT number only reveals its last four characters, unless
  the user holds the PII Reader role.
- **Failed**: bulk resubmit or skip.
- **Heartbeat**: both adapters alive.
- **Monitor**: the wall display.
- **Access**: Entra ID sign-in with Reader, Contributor and Owner roles.

*Optional:*
- If an MCP client is configured, ask the read-only operator endpoint (`/mcp`): *"Which quote
  requests failed today, and why?"*
- Mention `nb catalog export`: the same contract as a browsable EventCatalog site.

## Scene 8 — From demo to production, and the pilot (3 min)

*Say, using the [README table](../README.md#from-demo-to-production):*
- **Dynamics 365 → NimBus.** A Dataverse Service Endpoint and an async plug-in step put changes on
  Service Bus. The NimBus Dataverse adapter (preview) turns them into the same messages you saw.
  Changes made by the integration user are filtered out, so nothing echoes back.
- **Business Central → NimBus.** BC webhooks notify about a change (about 30 s after it, and
  subscriptions must be renewed every 3 days). The integration fetches the record through the
  standard API. A small **AL extension** adds the CRM reference fields and the prospect-quote API,
  because the standard API only quotes existing customers.
- **Hosting.** Everything runs in your Azure tenant: Service Bus, adapters on Container Apps or
  Functions, the operator console, and a SQL or Cosmos audit store. It is deployed and upgraded with
  the `nb` CLI.
- **The pilot.** Go live with one sales office: its sellers are mapped to Business Central
  salespeople, and its existing Business Central customers are loaded into CRM through the same
  pipeline. Other offices follow without changing the integration. The retry timings you saw are
  stage-tuned seconds; production uses minutes.

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
    prospects that aren't customers yet;
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
- The contracts are versioned code, and the platform validates them when it deploys (for example,
  a command must have exactly one receiver).
- The catalog is exportable as AsyncAPI or EventCatalog for review.

**"Can we see everything that happened to one customer?"**
- Yes. The integration trail links on the CRM forms open that customer's session in nimbus-ops,
  and the CRM timeline shows what the integration changed.

## If something misbehaves live

- **A scene doesn't react within ~10 s.** Check the Aspire dashboard: are the adapters running? The
  web clients poll every few seconds; refresh the page.
- **Leftovers from a rehearsal** (a failed message, an open circuit). Restart the AppHost: it resets
  everything in about 2 minutes. With a real namespace, purge the demo subscriptions first.
- **Circuit still Open after the window.** It closes on the next successful probe. Retries land
  about 30 s after the failures. Keep talking through 6b's "nothing is lost" point.

## Cheat sheet (seed data)

| Record | Use |
|---|---|
| Lead *ROV winch upgrade for research vessel* (Tailspin Marine Research) | Scenes 1–4 |
| *Fabrikam Offshore Energy* (C00010), existing customer | Scene 5 credit check |
| *OPP-10016* Trey Research Vessels, owner *Robin Hale* (no BC salesperson) | Scene 6a |
| *OPP-10012* Northwind Ocean Survey (C00020), owner *Maya Lindqvist* | Scene 6c |
| *OPP-10017* Proseware Cable Systems, owner *Maya Lindqvist* | Scene 6d |
| *OPP-10013* Litware Renewables, quote *S-QUO1001* already sent | Spare: a customer quote you can Make Order on |
| *OPP-10099* Wingtip Marine (warm-up) | Warm-up only |
