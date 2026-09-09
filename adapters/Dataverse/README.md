# NimBus Dataverse adapter (preview)

An optional platform extension with a .NET 10 isolated Azure Function App. Receives JSON RemoteExecutionContext messages on a non-session queue and publishes versioned record events through NimBus. Source lives here; customers use a tested deployment bundle.

**Not yet production-qualified:** fixtures are synthetic, source OperationId stability across Dataverse reposts is unverified, and Azure deployment has not been exercised. Do not claim exactly-once delivery or source commit ordering. Deployment leaves the trigger disabled until the tenant smoke test is completed.

## Build and test

From the repository root:

```powershell
dotnet build adapters/Dataverse/NimBus.Adapters.Dataverse.sln -c Release
dotnet test adapters/Dataverse/tests/NimBus.Adapters.Dataverse.Tests/NimBus.Adapters.Dataverse.Tests.csproj -c Release --no-build
```

Run Functions locally with Core Tools and a reachable host storage account/Azurite. Copy `src/NimBus.Adapters.Dataverse.Functions/local.settings.example.json` to ignored `local.settings.json`; replace the organization and namespaces, and configure table/column projections. Authenticate using your Azure development identity with only the required data roles. The output SDK uses DefaultAzureCredential; Functions uses its identity-based input binding. No source CRM SDK or Dataverse API credentials are needed by the Function App.

## Catalog and installation

Reference the Contracts package in your customer platform catalog and register `DataverseEndpoint` using your existing Platform composition. It declares all three v1 event types. Provision the NimBus publisher topic, forwarding subscriptions and downstream session-enabled subscriber endpoints using the normal NimBus topology workflow before enabling ingress. If you customize PublisherEndpoint, provide a matching catalog declaration; changing configuration alone does not provision topology.

The adapter has no NimBus subscriptions and should not be enrolled as a subscriber heartbeat target. Its ingress health is monitored through Functions and Service Bus.

See [registration](docs/dataverse-registration.md), [operations](docs/operations.md), [technical design](docs/TDD.md), [contracts](docs/events.md), and [compatibility](docs/compatibility.md).

## Deployment

The Bicep template references existing ingress and NimBus namespaces and an existing publisher topic in the target resource group. It creates a dedicated queue with Send-only SAS policy, Function App, user-assigned identity, host/deployment storage and Application Insights. It does not alter namespace networking or create subscriber topology. The namespaces may differ. Cross-resource-group deployment is not implemented in this first template.

Use the bundle's `deploy/deploy.ps1` with a parameter file copied from `parameters.example.json`. No source checkout is required. Dataverse SAS credentials must be obtained and entered through your secure administrative process; they are never template outputs. Ensure SAS is enabled on the ingress namespace and its network policy permits the actual Dataverse source path.

Build source-free preview artifacts with `deploy/build-bundle.ps1 -OutputDirectory <new-directory>`. Use `-NimBusVersion <published-version>` to test published SDK dependencies and produce distributable NuGet packages as well. Local-reference bundles are explicitly marked development builds and do not advertise a released compatibility range.

Reusing an output directory is rejected to prevent stale binaries or manifest hashes. The package contains the Function zip, deployment files, docs and SHA-256 manifest. Deployment validates the manifest before changing Azure resources. No publishing or deployment runs as part of local implementation verification.
