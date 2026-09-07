# Implementation plan

1. Verify action release manifests use Node 24 and check Aspire bundle guidance.
2. Correct the three model declarations and opt the demo into the CLI bundle.
3. Update only the CRM/ERP workflow's action versions.
4. Build the affected abstractions and AppHost, verify the targeted warnings are
   absent, and run existing platform tests plus workflow lint.
5. Report the exact scope of warnings resolved and any remaining verification.

## Verification

- Fresh abstractions build and demo AppHost build succeeded; the nine targeted C#
  diagnostics and ASPIRE010 were absent. The AppHost build used SkipSpaBuild because
  this change does not touch frontend assets.
- Nine existing platform/heartbeat contract tests passed in Release.
- Verified Node 24 in all four upstream action manifests; actionlint and diff
  whitespace checks passed.
- Other pre-existing compiler/analyzer warnings remain outside this scoped change.
