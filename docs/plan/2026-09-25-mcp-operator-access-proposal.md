# MCP operator access: proposal record

Status: design proposal (rev 2); no platform implementation. Spec:
[docs/spec/035-mcp-operator-access](../spec/035-mcp-operator-access/spec.md).

## What was produced

1. Inventoried WebApp operator workflows, role checks, message settlement and failure
   classification.
2. Verified MCP transport and authorization guidance against the 2026-07-28 specification.
3. Proposed an explicit operator tool surface, a shared application boundary, Entra access and
   mutation semantics, with gaps and deferred functionality.
4. Produced the interactive design (`design.html`) and the decision record (`spec.md`).

Constraints: preserve endpoint authorization and separate PII access; expose no administrative
tools; reuse the Resolver and existing application behavior; distinguish observed behavior from
proposed changes.

## Rev 2 changes (review, 2026-09-25)

- **Replaces `NimBus.Mcp`.** Added the tool-by-tool mapping, client migration and the removal
  checklist. Bus participation moves to `NimBus.Agents`, not MCP.
- **Hosted in Azure.** The WebApp's App Service site from `webApp.bicep`; noted how Spec 034 private
  mode limits reachability.
- **Deduplication corrected.** Topic duplicate detection is deliberately off, because re-sends keep
  their `MessageId`, and no operator-command deduplication exists. Receiver-side receipts are
  required.
- **Phase 2 split.** 2a uses a current-state guard for interactive callers, with no new store. 2b
  adds the operation journal and `nimbus_get_operation` before unattended mutations.
- **Authentication.** A separately named JWT scheme for `/mcp` instead of the existing `"Az"` policy
  scheme.
- **Local Aspire mode.** No sign-in when the existing local-dev bypass is active, reusing its
  Development-only double gate; loopback and `Origin` validation required. Entra everywhere else.
- **Rate limiting.** Extends the existing per-user `RateLimiting/` policies.
- **Evidence.** Links retargeted to master after the `f1dc8120` split (`EventImplementation.OperatorActions.cs`,
  `Startup.Security.cs`), with new entries for the replaced server and the deduplication constraint.
- **Tool contract.** `nimbus_set_message_reported` accepts the optional ticket ID.

## Verification

- All 22 repository links and 7 section anchors in `design.html` resolve on master `7e4860d5`.
- Browser check of `design.html`: no console errors, tool filters, architecture selection and
  recovery walkthrough, at desktop and 390 px widths.
- No .NET or frontend builds were run: this is a documentation-only change. Builds, live SQL/Cosmos
  conformance and Service Bus/Resolver integration tests are acceptance requirements for the
  implementation.
