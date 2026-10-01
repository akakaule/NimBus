# Failed-page control regression fixes

Fix the three defects identified in the recent-change review in one PR.

1. Add failing regressions for switching between items and table, clearing a brushed
   window by reselecting the current preset, closing or navigating away from a panel
   during pagination, and Shift+Tab from the initially focused dialog.
2. Apply all changed filter values before considering an unchanged-search refresh.
3. Discard pending Next navigation when its originating panel is no longer open.
4. Wrap backward keyboard navigation from the dialog to its last control.
5. Run the frontend suite/build and solution Release build/tests; report unavailable
   live integration coverage. Update MEMORY.md with the correction patterns.

Keep existing uncommitted work out of the commit. These fixes change interaction
behaviour without changing layout or introducing a new WebApp feature.

## Verification

- Six regression cases failed before implementation; all 26 focused tests passed afterward.
- Full frontend suite: 534 passed; ESLint passed for the four changed TypeScript files.
- `dotnet build src/NimBus.sln -c Release` passed with two existing ASPIRE010 warnings,
  including NSwag generation and the production SPA build. The initial attempt hit stale
  SPA asset paths while copying to WebApp.Tests; rerunning against the refreshed bundle passed.
- `dotnet test src/NimBus.sln -c Release --no-build`: 3,046 passed, 392 skipped, zero failures
  across 21 test projects. SQL, Cosmos and Azure Service Bus integration tests skipped because
  their connection configuration was absent; the local emulator suite passed.
