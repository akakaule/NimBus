# Plan: Dynamics 365 ↔ Business Central demo — the quote-linkage V1 flows

## Context

`samples/DynamicsBcDemo` (#171, #172) modelled CRM *asking* Business Central for a quote: a seller
clicked **Request quote in Business Central** and a `CreateBcSalesQuote` command made BC draft it.
Talking the demo through with prospective users showed that a typical first version of a Dynamics 365
Sales ↔ Business Central integration looks different:

| V1 flow | Direction |
|---|---|
| Initial sync of customers | BC → CRM |
| Initial sync of contacts | BC → CRM |
| Prospect → buying customer | CRM → BC and BC → CRM |
| Product groups | BC → CRM |
| Opportunities available in BC for quote linkage | CRM → BC |
| Quote status back on the deal after closure | BC → CRM |
| Order and invoice history | not needed |

With these ownership rules:
- CRM creates accounts and opportunities.
- An account is locked in CRM, and managed in BC, **once it has received a quote**.
- Products stay in BC; only product groups go to CRM.
- **Quotes are created in BC by a BC user and linked to a CRM opportunity.**

Outcome: the demo shows exactly these flows, and the failure-handling scenes (6a–6d) are rebuilt on
them.

## Message catalog after the change

Every message is session-keyed on the CRM account (`AccountId`), with two exceptions:
- the credit check has no session key;
- item categories are keyed on their `Code`.

| Message | Kind | From → to | Purpose |
|---|---|---|---|
| `D365ProspectUpdated` | Event | D365 → BC | **Changed.** Published when a prospect is created (qualify, burst) or edited while CRM owns it. Adds `PrimaryContact` (`ContactPersonDetails`, `[Sensitive]`). BC **upserts** the prospect contact and still answers 409 once the contact is a customer. |
| `D365OpportunityUpdated` | Event | D365 → BC | **New.** Carries AccountId, OpportunityId, OpportunityNumber, Name, AccountName, `BcCustomerId?`, SellerEmail, SellerName?, `EstimatedValue?`, CurrencyCode, `EstimatedCloseDate?`, `ProductGroupCode?`, Status (Open/Won/Lost) and UpdatedAt. BC upserts its "CRM opportunity". An unknown seller gives a 422 with an actionable message, which drives scene 6a. |
| `D365CreditCheckRequested` → `BcCreditStatus` | Request/reply | D365 ↔ BC | Unchanged. It becomes an optional scene, a "V2 idea". |
| `BcSalesQuoteCreated` / `Updated` | Event | BC → D365 | Now raised **only for quotes linked to a CRM opportunity**. |
| `BcCustomerCreated` / `Updated` | Event | BC → D365 | Unchanged. The initial sync also uses `BcCustomerUpdated`. |
| `BcContactUpdated` | Event | BC → D365 | **New.** A BC person contact of a customer: ContactId, Number, FirstName, Surname, Email, Phone, JobTitle, CompanyContactId, CompanyName, CustomerId?, CustomerNumber?, CrmAccountId? and ChangedAt, with personal data marked `[Sensitive]`. The session is the company's CRM account, or failing that the customer id, so a contact follows its customer. |
| `BcItemCategoryUpdated` | Event | BC → D365 | **New.** A product group (ItemCategoryId, Code, Description, ChangedAt). `[SessionKey(Code)]`. |

**Removed:**
- `CreateBcSalesQuote` (command) and its `QuoteRequestLine` class;
- `BcSalesOrderCreated`, because order history is not needed.

**Deterministic MessageIds from D365:**
- `prospect:{accountId:N}:{ModifiedOn.UtcTicks}`
- `opportunity:{opportunityId:N}:{ModifiedOn.UtcTicks}`

Delivering the same change again is therefore a duplicate, which scene 6d shows.

## Seed data after the change (`Contracts/Demo/SeedData.cs`)

**CRM at start: the "go-live morning".**
- Prospect **Proseware** with OPP-10017 (Maya).
- The warm-up customer **Wingtip** with OPP-10099 (Alex), pre-linked to a new BC customer C00090 so that the warm-up credit check works.
- The leads:
  - Tailspin (Alex);
  - Relecloud (Maya);
  - **City Power & Light, now owned by Robin Hale** (scene 6a).
- No product groups.
- These are **removed** from the CRM seed:
  - Fabrikam, Northwind, Litware and Adatum, which now arrive through the initial sync;
  - Trey Research;
  - OPP-10011/12/13/16;
  - all product lines and CRM products.

**BC at start:**
- Salespeople: 10, with Robin still missing.
- 5 item categories:
  - WINCH (winches and launch & recovery);
  - CONNECT (connectors and rotary joints);
  - CABLE;
  - SENSOR (sensors and cameras);
  - SERVICE.
  Every item gets a category.
- Customers:
  - Fabrikam, Northwind, Litware and Adatum are BC-native (`CrmAccountId` null);
  - Wingtip C00090 is linked.
- Company contacts: one per customer, plus the Proseware prospect (with CRM account id).
- Person contacts:
  - the existing primary persons;
  - a second person for Fabrikam and for Northwind.
- CRM opportunities: OPP-10017 and OPP-10099.
- S-QUO1001 stays as a **BC-only** quote (unlinked, so no events).

## Changes by project

### Contracts
- Apply the catalog above: new event classes with `[Description]` and `static Example`.
- Endpoints: update Produces/Consumes in `D365SalesEndpoint.cs` and `BusinessCentralEndpoint.cs`.
- Reshape SeedData:
  - item categories and `Item.CategoryCode`;
  - drop Opportunity `Lines`;
  - a BC customer for Wingtip;
  - extra persons;
  - flags for what each system seeds.

### BusinessCentral.Api
- **Entities:**
  - `ItemCategory`, and `Item.ItemCategoryCode`;
  - `Contact` gains `FirstName`, `Surname`, `JobTitle`, `Email` and `CompanyContactId` (`Type` is Company or Person);
  - `CrmOpportunity`: Id = the CRM id, Number, Name, CrmAccountId, AccountName, CustomerId?, SalespersonCode, EstimatedValue?, CurrencyCode, EstimatedCloseDate?, ItemCategoryCode?, Status, LastModifiedDateTime;
  - drop `SalesQuote.CrmRequestRevision`.
- **`Domain/SalesService.cs`:**
  - Remove `HandleQuoteRequestAsync` and the `QuoteRequest*` types.
  - `UpsertProspectAsync`: creates the contact (numbered from the Contact series, template by country) or updates it. 409 once converted.
  - `UpsertCrmOpportunityAsync`: salesperson by e-mail, otherwise 422 `Application_SalespersonNotFound` with today's message. Resolves the customer (it links `Customer.CrmAccountId` when `BcCustomerId` is given and the customer isn't linked yet). Raises no events.
  - `CreateQuoteFromOpportunityAsync(crmOpportunityId, events)`:
    - Checks: 404 when unknown; 409 when the opportunity isn't Open.
    - Sell-to: the customer, or else the prospect contact (422 `Application_ProspectNotFound`).
    - Quote fields: salesperson from the opportunity; ExternalDocumentNumber is the opportunity number and Description its name; `CrmOpportunityId` and `CrmAccountId` are set.
    - Starts with no lines and raises `QuoteCreated`.
  - `SendAsync` and `MakeOrderAsync` refuse quotes with no lines (422 `Application_NoLines`).
  - MakeOrder raises only CustomerCreated (when it converts) and QuoteUpdated, with no `OrderCreated`. It also marks BC's own `CrmOpportunity` as Won, because CRM never echoes the win it receives from the integration.
  - `BuildInitialSyncAsync(events)`: item categories, then customers, then person contacts of customers.
- **`Domain/BcEvents.cs`:**
  - quote events only when `CrmOpportunityId` is set;
  - new `ContactUpdated` and `ItemCategoryUpdated` builders;
  - remove `OrderCreated`.
- **`Endpoints/IntegrationApiEndpoints.cs`:**
  - remove `POST quoteRequests`;
  - `PUT prospects({crmAccountId})` (upsert);
  - new `PUT crmOpportunities({id:guid})`.
- **`Endpoints/AppEndpoints.cs`:**
  - `GET /crm-opportunities`, with the latest linked quote's number and status;
  - `POST /quotes {crmOpportunityId}`, which returns 201 with QuoteDetail;
  - QuoteDetail gains `crmOpportunity {number, name, status}`;
  - contacts include persons with their company name;
  - items include the category;
  - the role centre gains `crmOpportunitiesWithoutQuote`;
  - the role centre's "prospects" counts only company contacts without a customer, not person contacts.
- **`Endpoints/DemoEndpoints.cs`:** `POST /api/demo/initial-sync` runs in `BcUnitOfWork` (outbox) and returns counts.
- **`Data/BcDatabaseInitializer.cs`:** the new seed. Seeded contacts use numbers below CT000101.

### BusinessCentral.Adapter
- **Client:**
  - remove `CreateQuoteRequestAsync`;
  - `UpsertProspectAsync`: 409 becomes an outcome, not a failure;
  - `UpsertCrmOpportunityAsync`: 422 is thrown as `BcRequestRejectedException`, so the message fails and waits for an operator.
- **Handlers:**
  - remove `CreateBcSalesQuoteHandler`;
  - `D365ProspectUpdatedHandler` upserts, including the contact person;
  - new `D365OpportunityUpdatedHandler`.
- **Program.cs:** scan handlers by `D365ProspectUpdatedHandler`. Inbox, retries and alerts stay unchanged.
- **Circuit breaker:** the sampling window drops from 60 s to 10 s. A new account now sends two messages; during an
  outage only the first fails and the second is deferred behind it, so the successes of the previous scene must
  not dilute the failure rate (found in the live run: a burst straight after scene 6a left the circuit Closed).

### D365Sales.Api
- **Entities:**
  - remove `Product`, `OpportunityProduct`, `Opportunity.Lines`, `CsLinesRevision`, `CsQuoteRequest*` and `CsBcOrderNumber`;
  - add `ProductGroup` (Id = the BC item category id, CsCode, Name, LastSyncedOn);
  - add `Opportunity.CsProductGroupId`;
  - add `Contact.CsBcContactId` (unique, filtered).
- **`Domain/SalesService.cs`:**
  - `OpportunityEdit` gains EstimatedValue and ProductGroupId (validated);
  - remove `SetLinesAsync`;
  - `UpdateAccountAsync` notifies BC whenever CRM owns the account;
  - the BC-owned refusal text differs for a prospect ("managed by Business Central since its first quote") and for a customer.
- **`Integration/CrmChangePublisher.cs`:** replaces `QuoteRequestPublisher`. It builds prospect and opportunity events from the database state, with the deterministic MessageIds above. The session is the account; the correlation is the opportunity or the account.
- **`Endpoints/AppEndpoints.cs`:**
  - qualify publishes the prospect, then the opportunity;
  - `PUT /accounts` (CRM-owned) publishes the prospect;
  - `PUT /opportunities` publishes the opportunity;
  - add `GET /productgroups`;
  - remove `/products`, `/lines` and `/request-quote`.
- **`Endpoints/DataverseApiEndpoints.cs`:**
  - `PATCH /contacts(cs_bccontactid={id})` upsert. It binds the parent through `/accounts({id})` or the alternate key `/accounts(cs_bccustomerid={id})`, and sets the primary contact when the account has none.
  - `PATCH /cs_productgroups(cs_productgroupid={id})` upsert.
  - A timeline entry when `cs_masterdataowner` flips to BC on a prospect.
  - Drop `cs_bcordernumber`.
- **`Endpoints/DemoEndpoints.cs`:**
  - the burst creates prospects, contacts and opportunities, and publishes the prospect and opportunity events;
  - new `POST /api/demo/redeliver` republishes the **last published** opportunity event, kept in memory, with the same MessageId (scene 6d). It does not rebuild the event from the database, because integration writes also bump `ModifiedOn`.

### D365Sales.Adapter
- **`QuoteMirror`:**
  - upserts the mirror and patches the opportunity's `cs_bcquote*` columns (it no longer sets estimated value or stage);
  - patches the account to `cs_masterdataowner = 2`, which is the lock at the first quote, plus the contact number;
  - on **Accepted**, calls `WinOpportunityAsync` with the quote total and date.
- **New mirrors:** `ContactMirror` and `ProductGroupMirror`, with the matching `IDataverseClient` upserts.
- **Removed:** `BcSalesOrderCreatedHandler`.

### D365Sales.Web (subagent)
- Remove:
  - the Request quote button;
  - the Product lines tab (`LinesTab.tsx`);
  - the 'Requested' state;
  - lines and order texts.
- Summary tab:
  - editable **Est. revenue**;
  - a **Product group** dropdown fed from `GET /productgroups` (empty state: "Product groups come from Business Central").
- Quote strip:
  - "No quote yet — Business Central makes the quote from this opportunity";
  - otherwise the quote, its status and total, and a link to BC.
- Won banner: "Won — Business Central quote … accepted".
- Account lock banner: a prospect variant ("since its first quote").
- Lead, account and dashboard copy updated.
- Integration trail: "Changes sent to Business Central" / "Updates from Business Central".

### BusinessCentral.Web (subagent)
- New **CRM Opportunities** page and navigation entry:
  - columns: No., Name, Account (Prospect/Customer tag), Salesperson, Product group, Est. value, Close date, Status and linked quote;
  - a **Create sales quote** action per row.
- Sales Quotes gets **New**: a dialog that picks an open CRM opportunity, then opens the new quote card.
- Quote card:
  - the CRM opportunity number and name on the CRM link tab;
  - an empty-lines hint pointing at Edit lines. The existing `QuoteLinesPart` already adds lines with an item picker.
- Contacts list shows persons and their company.
- Role centre: a "CRM opportunities without a quote" cue.
- Cockpit:
  - a **Go-live: initial sync to Dynamics 365** section with the result counts;
  - the burst becomes "Create opportunities now";
  - a **Deliver the last opportunity change again** button through `/d365-api`.

### Tests (`tests/DynamicsBcDemo.Tests`)
- **Catalog:** new session-key rule (item categories keyed on Code); seed consistency across both systems.
- **BC domain:**
  - prospect upsert, create, update and 409;
  - opportunity upsert: 422 with a missing seller, and customer linking;
  - quote from an opportunity, for a prospect and for a customer;
  - unlinked quotes raise no events;
  - no lines means no send and no make order;
  - MakeOrder raises exactly `[BcCustomerCreated, BcSalesQuoteUpdated]`;
  - initial sync order.
- **Adapter:** retry rules against `D365OpportunityUpdated`; 422 and 409 mapping.
- **D365:**
  - ownership for a BC-owned prospect;
  - `CrmChangePublisher` MessageIds (same state gives the same id; a new change gives a new id);
  - qualify publishes the prospect before the opportunity;
  - BC-owned edits don't publish.
- **New:** reference `D365Sales.Adapter` and test `QuoteMirror` with a fake `IDataverseClient` (lock at the first quote; win on Accepted).

### Docs
- **README:**
  - ownership and architecture diagrams;
  - "What the demo shows";
  - message catalog;
  - sequence diagram (opportunity → BC user quote → accepted → won);
  - demo controls;
  - layout.
- **`docs/talk-track.md`:** the prep, scenes and cheat sheet below.
- **Plans:** a new `docs/plan/2026-09-28-dynamics-bc-demo-v1-flows.md` (this plan, generic wording), and a one-line "partly superseded" note on the old plan.

## Scenes after the change
0. The ownership model.
1. **Go-live: initial sync.** Cockpit → Initial sync. CRM gains customers (BC-owned, locked), their contacts and the product groups; nimbus-ops shows the load. (V1 flows 1, 2 and 4.)
2. **Lead → opportunity.** Qualify Tailspin and set the product group. BC then shows the prospect contact and OPP-10025 under CRM Opportunities. (V1 flows 3 and 5.)
3. **The quote is made in BC.** CRM Opportunities → Create sales quote → add lines → Send. The CRM opportunity shows the quote, and the account locks: "managed by BC since its first quote".
4. **Won.** Make Order converts the prospect into a customer from a template. The CRM account becomes a Customer, and the opportunity is **won from the accepted quote**. No order is synced. (V1 flow 6.)
5. *(Optional, V2 idea)* Check credit.
6. Failure handling:
   - **6a:** Robin qualifies City Power & Light. The opportunity fails because no salesperson matches, and an alert appears. Add the salesperson in BC and Resubmit, and it appears in BC; the burst flows meanwhile.
   - **6b:** an update window during the burst opens the circuit, which closes again with nobody acting.
   - **6c:** throttling while Maya edits OPP-10017; it completes on its own and the circuit stays Closed.
   - **6d:** redeliver the last change, which is skipped as DuplicateDetected.
7. The nimbus-ops tour: PII masking on `D365ProspectUpdated`.
8. From demo to production.

**Warm-up:**
- credit check on Wingtip;
- edit OPP-10099 so it syncs to BC;
- create a quote on it in BC.

## Implementation order

1. Contracts and SeedData, then the BC API and adapter, then the D365 API and adapter, then the
   tests, building as we go.
2. The two web clients, in parallel, against the API shapes above; `npm run build` must pass.
3. README and talk track.
4. A live run of every scene on a fresh stack (below), then commits by area (Conventional Commits)
   and one PR with screenshots of fictional data.

## Verification
- `dotnet build src/NimBus.sln -c Release`: 0 errors and no CS warnings in the demo projects.
- `dotnet test tests/DynamicsBcDemo.Tests -c Release --no-build`.
- `npm run build` and `npm audit --package-lock-only` in both SPAs.
- **Live, scripted through the APIs** (extend the scratchpad scripts):
  - initial-sync counts arrive in CRM;
  - qualify: BC has the prospect and the CRM opportunity;
  - create quote: the CRM mirror appears and the account locks;
  - Send;
  - Make Order: customer and Won;
  - 6a: failed → salesperson added → resubmitted through the nimbus-ops API → completed;
  - 6b: open/closed, with every burst opportunity in BC;
  - 6c: completes and the circuit stays Closed;
  - 6d: DuplicateDetected;
  - final counts: nothing failed, pending or deferred.
- **Browser pass:**
  - the BC CRM Opportunities page, the New quote dialog, and the cockpit initial sync and redeliver;
  - the D365 product group, quote strip, lock banner and won banner.

## Out of scope
- Ongoing contact sync after the initial load, which is not in V1.
- Order and invoice history.
- Losing an opportunity from BC.
- BC → CRM edits of a BC-managed prospect before it becomes a customer.
- Real Dataverse or BC connectors.
