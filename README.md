# NimBus

```text
 _   _ _           ____             
| \ | (_)_ __ ___ | __ ) _   _ ___  
|  \| | | '_ ` _ \|  _ \| | | / __| 
| |\  | | | | | | | |_) | |_| \__ \ 
|_| \_|_|_| |_| |_|____/ \__,_|___/ 
```

NimBus is an Azure Service Bus based integration platform with a shared SDK, management web app, and message tracking and storage.

## Repository Layout

- `src/NimBus.sln` builds the full platform, including the web app, resolver, app host, and shared libraries.
- `src/NimBus.WebApp.sln` builds the management web application and the projects it depends on.
- `src/NimBus.SDK.slnx` builds the SDK-focused subset used for library development.

Key projects:

- `src/NimBus.Core`: shared endpoint, event, message, and pipeline abstractions.
- `src/NimBus`: platform configuration and built-in endpoint definitions.
- `src/NimBus.CommandLine`: `nb` CLI for Azure infrastructure, topology provisioning, and deployment.
- `src/NimBus.SDK`: publisher/subscriber SDK surface.
- `src/NimBus.ServiceBus`: Service Bus integration layer.
- `src/NimBus.MessageStore`: Cosmos DB backed message and state storage.
- `src/NimBus.Manager`: management client abstractions used by the web app.
- `src/NimBus.WebApp`: ASP.NET Core management UI plus the React/Vite client app.
- `src/NimBus.Resolver`: tracks message outcomes and updates resolver state.
- `src/NimBus.AppHost`: Aspire host for local orchestration.
- `src/NimBus.Testing`: in-memory test transport for running the full pipeline without Azure.
- `src/NimBus.Outbox.SqlServer`: SQL Server transactional outbox implementation.
- `samples/AspirePubSub/`: sample publisher, subscriber (with middleware + DeferredProcessor), provisioner, and resolver worker.

### Extensions

NimBus uses an extension framework to separate core messaging from optional features. Extensions are registered through the `AddNimBus()` builder and can hook into the message pipeline and lifecycle events.

- `src/NimBus.Extensions.Notifications`: sends notifications on message failures and dead-letters.

See [docs/extensions.md](docs/extensions.md) for the full guide on using and creating extensions.

## NuGet Packages

| Package | Description |
|---------|-------------|
| `NimBus.Abstractions` | Core abstractions and interfaces |
| `NimBus.Core` | Endpoint management, retry policies, logging |
| `NimBus.ServiceBus` | Azure Service Bus integration |
| `NimBus.SDK` | Publisher/subscriber SDK |
| `NimBus.CommandLine` | `nb` CLI tool |

### Install

```shell
dotnet add package NimBus.SDK
dotnet tool install -g NimBus.CommandLine
```

### Publishing

Push a version tag to trigger the [NuGet publish workflow](.github/workflows/nuget-publish.yml):

```shell
git tag v1.0.0
git push origin v1.0.0
```

Pre-release versions are supported (e.g. `v1.0.0-preview.1`).

## Prerequisites

- .NET 10 SDK preview, matching the project target frameworks.
- Node.js, required by `src/NimBus.WebApp` during build.
- Access to NuGet package sources used by the solution.

## Build

From the repository root:

```powershell
dotnet build .\src\NimBus.CommandLine\NimBus.CommandLine.csproj
dotnet build .\src\NimBus.SDK.slnx
dotnet build .\src\NimBus.WebApp.sln
dotnet build .\src\NimBus.sln
```

Notes:

- `src/NimBus.WebApp` runs `npm install` and `npm run build` as part of the .NET build.
- `NSwag.MSBuild` is used directly from NuGet; no local `dotnet-tools.json` manifest is required.

## CLI

The repository includes a `dotnet` tool named `nb` in `src/NimBus.CommandLine`.
It handles Azure infrastructure provisioning, Service Bus topology setup, and application deployment.

### Prerequisites

- Azure CLI (`az`) installed and on `PATH`
- `az login` completed for the target subscription
- Permissions to create and deploy resources in the target resource group

### Running the tool

Run via `dotnet run` from the repository root:

```powershell
dotnet run --project .\src\NimBus.CommandLine -- <command> [options]
```

Alternatively, install it as a local dotnet tool:

```powershell
dotnet pack .\src\NimBus.CommandLine
dotnet tool install --global --add-source .\src\NimBus.CommandLine\nupkg NimBus.CommandLine
nb <command> [options]
```

Use `--help` on any command to see all available options:

```powershell
dotnet run --project .\src\NimBus.CommandLine -- --help
dotnet run --project .\src\NimBus.CommandLine -- infra apply --help
```

### Quick start: full deployment

To provision infrastructure, set up the Service Bus topology, and deploy the applications in one step:

```powershell
dotnet run --project .\src\NimBus.CommandLine -- setup `
  --solution-id nimbus `
  --environment dev `
  --resource-group rg-nimbus-dev
```

This runs `infra apply`, `topology apply`, and `deploy apps` in sequence.

### Commands

#### `infra apply`

Deploys Azure infrastructure (Service Bus namespace, Cosmos DB, Application Insights, Function App, Web App) using the bicep templates in `deploy/bicep/`.

```powershell
dotnet run --project .\src\NimBus.CommandLine -- infra apply `
  --solution-id nimbus `
  --environment dev `
  --resource-group rg-nimbus-dev
```

| Option | Required | Description |
|--------|----------|-------------|
| `--solution-id <ID>` | Yes | Identifier used in Azure resource names |
| `--environment <NAME>` | Yes | Environment name (e.g. `dev`, `test`, `prod`) |
| `--resource-group <NAME>` | Yes | Azure resource group to deploy into |
| `--repo-root <PATH>` | No | Repository root; auto-detected if omitted |
| `--location <AZURE-REGION>` | No | Azure region override for the bicep templates |
| `--webapp-version <VALUE>` | No | Version string stored in the web app settings |

#### `topology export`

Exports the current `PlatformConfiguration` (endpoints, event types, routing) to a JSON file for inspection.

```powershell
dotnet run --project .\src\NimBus.CommandLine -- topology export
dotnet run --project .\src\NimBus.CommandLine -- topology export -o .\my-config.json
```

| Option | Required | Description |
|--------|----------|-------------|
| `-o`, `--output <PATH>` | No | Output path; defaults to `platform-config.json` in the current directory |

#### `topology apply`

Provisions Service Bus topics, subscriptions, and routing rules based on `PlatformConfiguration`. This creates the messaging topology that the platform endpoints use to communicate.

```powershell
dotnet run --project .\src\NimBus.CommandLine -- topology apply `
  --solution-id nimbus `
  --environment dev `
  --resource-group rg-nimbus-dev
```

| Option | Required | Description |
|--------|----------|-------------|
| `--solution-id <ID>` | Yes | Solution identifier |
| `--environment <NAME>` | Yes | Environment name |
| `--resource-group <NAME>` | Yes | Resource group containing the Service Bus namespace |

#### `deploy apps`

Builds, packages, and deploys the Resolver (Azure Function App) and Web App to Azure.

```powershell
dotnet run --project .\src\NimBus.CommandLine -- deploy apps `
  --solution-id nimbus `
  --environment dev `
  --resource-group rg-nimbus-dev
```

| Option | Required | Description |
|--------|----------|-------------|
| `--solution-id <ID>` | Yes | Solution identifier |
| `--environment <NAME>` | Yes | Environment name |
| `--resource-group <NAME>` | Yes | Resource group containing the target apps |
| `--repo-root <PATH>` | No | Repository root; auto-detected if omitted |
| `--configuration <NAME>` | No | Build configuration passed to `dotnet publish`; defaults to `Release` |

#### `setup`

Runs the full provisioning and deployment pipeline in sequence: `infra apply` → `topology apply` → `deploy apps`. Accepts all options from those individual commands.

### Resource naming

The `--solution-id` and `--environment` values are normalized (lowercased, non-alphanumeric characters removed) and combined to produce Azure resource names:

| Resource | Naming pattern | Example |
|----------|---------------|---------|
| Service Bus namespace | `sb-{solutionId}-{environment}` | `sb-nimbus-dev` |
| Application Insights | `ai-{solutionId}-{environment}-global-tracelog` | `ai-nimbus-dev-global-tracelog` |
| Cosmos DB account | `cosmos-{solutionId}-{environment}` | `cosmos-nimbus-dev` |
| Resolver Function App | `func-{solutionId}-{environment}-resolver` | `func-nimbus-dev-resolver` |
| Management Web App | `webapp-{solutionId}-{environment}-management` | `webapp-nimbus-dev-management` |

## Local Development (Aspire)

The Aspire AppHost orchestrates the full platform locally. A built-in **Provisioner** creates the Service Bus topics/subscriptions before starting the Resolver and WebApp.

### 1. Set connection strings

```powershell
dotnet user-secrets set "ConnectionStrings:servicebus" "<your-servicebus-connection-string>" `
  --project .\src\NimBus.AppHost

dotnet user-secrets set "ConnectionStrings:cosmos" "<your-cosmos-connection-string>" `
  --project .\src\NimBus.AppHost
```

### 2. Run

```powershell
dotnet run --project .\src\NimBus.AppHost
```

The Aspire dashboard opens automatically. You'll see:

- **provisioner** — provisions Service Bus topology, then exits
- **resolver** — starts after provisioner completes
- **webapp** — starts after provisioner completes (external HTTP endpoint)
- **publisher** — sample HTTP API (`POST /publish/order`, `POST /publish/order-failed`)
- **subscriber** — sample event handler with middleware pipeline and separated DeferredProcessor

## CI/CD

### GitHub Actions

The repository includes a [`deploy.yml`](.github/workflows/deploy.yml) workflow triggered manually via `workflow_dispatch`.

1. **Configure OIDC** — set up [workload identity federation](https://learn.microsoft.com/entra/workload-id/workload-identity-federation) for your GitHub repo
2. **Set repository variables**: `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID`
3. **Trigger** the `Deploy NimBus` workflow from the Actions tab with your solution ID, environment, and resource group

### Azure DevOps

The repository includes an [`azure-pipelines-deploy.yml`](pipelines/azure-pipelines-deploy.yml) pipeline triggered manually.

1. **Create a service connection** for Azure in your Azure DevOps project
2. **Import the pipeline** from `pipelines/azure-pipelines-deploy.yml`
3. **Run the pipeline** with your solution ID, environment, resource group, and service connection name

The pipeline uses `nb setup` to run all deployment steps (`infra apply` → `topology apply` → `deploy apps`) in a single `AzureCLI` task.

## Documentation

| Guide | Description |
|-------|-------------|
| [Getting Started](docs/getting-started.md) | Step-by-step tutorial: create a publisher, subscriber, and run with Aspire |
| [Message Flows](docs/message-flows.md) | All 10 message flow patterns with ASCII diagrams |
| [Deferred Messages](docs/deferred-messages.md) | Session blocking and deferral mechanics with Mermaid diagrams |
| [Pipeline Middleware](docs/pipeline-middleware.md) | Built-in middleware, custom behaviors, and lifecycle observers |
| [CLI Reference](docs/cli.md) | All `nb` commands: infra, topology, deploy, endpoint, container, catalog |
| [SDK API Reference](docs/sdk-api-reference.md) | Interfaces: IPublisherClient, IEventHandler, RetryPolicy, IOutbox |
| [Extensions](docs/extensions.md) | Extension framework guide |
| [Architecture](docs/architecture.md) | System design and component overview |
| [Roadmap](docs/roadmap.md) | Feature roadmap through H2 2027 |

## License

This project and its solutions are licensed under the MIT License.
