# NimBus.WebApp

ASP.NET Core management API + React/Vite client app for NimBus operations.

## Local development

### Prerequisites

- .NET SDK matching the repo target frameworks
- Node.js (required for `ClientApp`)
- Access to Azure Service Bus and Cosmos DB (connection strings or identity-based endpoints)

### Configure settings

Set configuration in user secrets (`src/NimBus.WebApp`) or `appsettings.Development.json`.

Common options:

- `ConnectionStrings:servicebus` (or `AzureWebJobsServiceBus`)
- `ConnectionStrings:cosmos` (or `CosmosConnection`)
- `AzureWebJobsServiceBus__fullyQualifiedNamespace` and/or `CosmosAccountEndpoint` for managed-identity style configuration

### Run the backend

From repo root:

```powershell
dotnet run --project .\src\NimBus.WebApp
```

### Run the frontend (hot reload)

In a separate terminal:

```powershell
Set-Location .\src\NimBus.WebApp\ClientApp
npm install
npm run start
```

Vite runs on `https://localhost:3001` and proxies `/api`, `/hubs`, `/login`, and `/logout` to the WebApp backend.

If `ClientApp\devcert.crt` and `ClientApp\devcert.key` are present, Vite uses them for HTTPS; otherwise it falls back to default dev behavior.

## API contract and code generation

The project is OpenAPI-first:

- Spec: `api-spec.yaml`
- NSwag config: `api-gen.nswag`
- Generated server contract: `Controllers/ApiContract.g.cs`
- Generated client SDK: `ClientApp/src/api-client/index.ts`

To add endpoints:

1. Update `api-spec.yaml`
2. Implement generated interfaces in `Controllers/ApiContract/*Implementation.cs`
3. Build/run to regenerate NSwag outputs

## Frontend notes

The client app is React + TypeScript + Vite. See `ClientApp/README.md` for frontend-specific commands and structure.
