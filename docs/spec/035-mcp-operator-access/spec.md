# Spec 035 — MCP operator access

Status: design proposal (rev 2, 2026-09-25). Nothing is implemented.

The interactive design is in [design.html](design.html). It covers the architecture, the 21-tool
catalog, a recovery walkthrough, the security model, delivery phases and the code it is based on.
This file records the decisions.

## Summary

Add a remote MCP server for operators. It lets AI agents observe endpoint health, investigate
failed messages, classify failures and carry out single-message recovery through NimBus's existing
authorization, redaction, audit and Resolver command path.

## Decisions

1. **Host in Azure, inside the WebApp.** Serve an opt-in `/mcp` Streamable HTTP endpoint from the
   WebApp site that `deploy/bicep/templates/webApp.bicep` provisions, using
   `ModelContextProtocol.AspNetCore`. A separate Azure host stays possible later.
2. **Replace `NimBus.Mcp`.** The Spec 022 stdio server is retired once the Phase 1 read tools run in
   Azure. Bus participation (define event type, subscribe, receive, publish, agent settle) is not
   carried into MCP. Participant agents use the `NimBus.Agents` REST SDK. See "Replacing NimBus.Mcp"
   in the design for the tool-by-tool mapping.
3. **Entra in Azure, on its own bearer scheme.** Use a separately named JWT bearer scheme with
   its own App ID URI and `nimbus.*` scopes, bound to `/mcp` only. Do not use the WebApp's `"Az"`
   policy scheme or Identity cookies.
4. **No sign-in under Aspire.** When the existing local-dev bypass is active (Development and
   `EnableLocalDevAuthentication=true`; startup throws if the flag is set anywhere else), `/mcp`
   accepts tokenless calls as the "Local Developer" principal. Scope checks are skipped; role, PII
   and message-state checks still run. Keep it on loopback and enforce `Origin` validation against
   DNS rebinding. With the bypass off, MCP requires Entra.
5. **Operator tools only.** An explicit registry; no admin, topology, access-control, purge or
   generic API bridge.
6. **Mutations in two steps.** Phase 2a: interactive callers only, with a current-state guard
   (message still failed, same latest attempt) and audit before the side effect, with no new store.
   Phase 2b: operation journal, `nimbus_get_operation` and receiver-side command receipts, required
   before unattended workloads may mutate.
7. **Never enable topic duplicate detection.** The throttle and defer path re-sends messages with
   their original `MessageId` (`src/NimBus.ServiceBus/MessageContext.cs`). Deduplication must happen
   on the receiving side.

## Delivery

| Phase | Scope | Exit |
| --- | --- | --- |
| 1 Observe | Adapter, bearer scheme, local Aspire mode, read tools, classification reads | Read-only pilot in a nonproduction Azure environment; `NimBus.Mcp` removed |
| 2a Operate | Shared coordinator, preview, fresh ACL checks, state guard; classify, report, resubmit, skip | Delegated pilot; stale-state and UI-vs-agent race tests |
| 2b Operate | Operation journal (Cosmos, SQL, in-memory + conformance), receiver-side receipts | Crash/retry tests; bounded workload mutations |
| 3 Complete | Handoff settlement, deferred recovery, payload edits | Parity with existing UI semantics |

## Retiring `NimBus.Mcp`

`NimBus.Mcp` has `IsPackable=false`, so it ships no NuGet package or public API, and removing it is
not a versioning break. It still needs a release-notes entry. At the end of Phase 1:

- remove `src/NimBus.Mcp`, `tests/NimBus.Mcp.Tests` and their `src/NimBus.sln` entries;
- rewrite `docs/mcp-server.md` for the hosted server, including client configuration by URL;
- update `site/features.html`, Spec 034 §5.8 and the MCP question in Spec 032 (and its plan).

## Out of scope

- Authorization for the REST agent API (`/api/agent/*`), which currently checks no NimBus role and
  takes its audit actor from the `X-Agent-Id` header. It is tracked separately and outlives this
  server.
- An acknowledge/clear tool (shared Monitor acknowledgements exist server-side since #142; the
  read tools surface them), manual classification overrides, compose-new-event, bulk recovery.
