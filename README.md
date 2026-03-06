# NimBus

```text
 _   _ _           ____             
| \ | (_)_ __ ___ | __ ) _   _ ___  
|  \| | | '_ ` _ \|  _ \| | | / __| 
| |\  | | | | | | | |_) | |_| \__ \ 
|_| \_|_|_| |_| |_|____/ \__,_|___/ 
```

NimBus is an Azure Service Bus based integration platform with a shared SDK, endpoint metadata model, management web app, Aspire host, and Azure Functions resolver.

## Repository Layout

- `src/NimBus.sln` builds the full platform, including the web app, resolver, app host, and shared libraries.
- `src/NimBus.WebApp.sln` builds the management web application and the projects it depends on.
- `src/NimBus.SDK.slnx` builds the SDK-focused subset used for library development.

Key projects:

- `src/NimBus.Core`: shared endpoint, event, message, and logging abstractions.
- `src/NimBus`: platform configuration and built-in endpoint definitions.
- `src/NimBus.SDK`: publisher/subscriber SDK surface.
- `src/NimBus.ServiceBus`: Service Bus integration layer.
- `src/NimBus.MessageStore`: Cosmos DB backed message and state storage.
- `src/NimBus.Manager`: management client abstractions used by the web app.
- `src/NimBus.WebApp`: ASP.NET Core management UI plus the React/Vite client app.
- `src/NimBus.Resolver`: Azure Functions worker for resolver workflows.
- `src/NimBus.AppHost`: Aspire host for local orchestration.

## Prerequisites

- .NET 10 SDK preview, matching the project target frameworks.
- Node.js, required by `src/NimBus.WebApp` during build.
- Access to NuGet package sources used by the solution.

## Build

From the repository root:

```powershell
dotnet build .\src\NimBus.SDK.slnx
dotnet build .\src\NimBus.WebApp.sln
dotnet build .\src\NimBus.sln
```

Notes:

- `src/NimBus.WebApp` runs `npm install` and `npm run build` as part of the .NET build.
- `NSwag.MSBuild` is used directly from NuGet; no local `dotnet-tools.json` manifest is required.

## Current Status

The repository has been updated so all three solution entrypoints build successfully with the current in-repo project graph:

- stale solution entries removed from `NimBus.SDK.slnx`
- internal package references replaced with local project references where source exists
- deleted legacy project references removed from the management path

## License

Internal project. Add the intended license terms before publishing externally.
