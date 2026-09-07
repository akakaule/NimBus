# Implementation plan

1. Verify action release manifests use Node 24 and check Aspire bundle guidance.
2. Correct the three model declarations and opt the demo into the CLI bundle.
3. Update only the CRM/ERP workflow's action versions.
4. Build the affected abstractions and AppHost, verify the targeted warnings are
   absent, and run existing platform tests plus workflow lint.
5. Report the exact scope of warnings resolved and any remaining verification.
