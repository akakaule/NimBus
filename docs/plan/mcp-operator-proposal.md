# MCP operator proposal work plan

Status: proposal complete and HTML verified; no platform implementation performed.

1. Inventory WebApp operator workflows, role checks, message settlement, and failure classification.
2. Verify current MCP transport/authentication guidance against primary sources.
3. Propose an explicit operator tool surface, shared application boundary, secure access, and reliable mutation semantics; identify gaps and deferred functionality.
4. Produce a self-contained, interactive HTML design in `docs/spec/` with repository evidence and references.
5. Validate HTML behavior, links, desktop/mobile layout, and JavaScript errors. Record checks; no platform build is needed for documentation-only output.

Constraints: preserve endpoint authorization and separate PII access; expose no administrative tools; reuse the Resolver and existing application behavior; distinguish observed behavior from proposed changes. Keep unrelated untracked files intact.

## Deliverable

`docs/spec/mcp-operator-design.html`: standalone design with interactive architecture, a filterable 21-tool catalog, recovery simulation, security model, delivery phases, parity exceptions and source references. Recommended deployment is an opt-in MCP adapter inside the WebApp using shared operator application services.

## Verification (2026-09-25)

- Headless Chromium: all nine architecture selections; all four tool filters (21 total, 12 observe, 9 act, 4 extended).
- Both resubmit and skip simulations: normal, stale message, duplicate request, revoked permission and uncertain publish; previous-step navigation checked.
- All 12 repository links and six section links resolve.
- No page-level horizontal overflow at widths 1440, 1024, 736, 390 and 320 pixels. Verified the architecture control remains clickable at 320 pixels.
- Desktop and mobile screenshots visually inspected; mobile decorative connector overlap corrected.
- JavaScript syntax and browser runtime checks passed with zero page errors. Git whitespace check passed.
- .NET/frontend application builds and SQL/Cosmos/Service Bus integration tests were not run: the changes are a standalone proposal and work-plan document, with no platform code changes. These remain acceptance requirements for a future implementation.

The HTML simulation verifies the explanatory artifact only; it does not establish that the proposed MCP server or recovery guarantees are implemented.
