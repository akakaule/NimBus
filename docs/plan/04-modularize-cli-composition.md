# Plan 4: Modularize CLI composition

## Goal

Turn `NimBus.CommandLine/Program.cs` into a small entry point plus cohesive top-level command modules. Centralize duplicated deployment option binding and make the command graph testable without changing command names, flags, defaults, help text, environment-variable secret handling, or exit codes.

## Scope

The command groups currently configured in `Program.cs` are in scope:

- `infra`
- `topology`
- `deploy`
- `setup`
- `endpoint`
- `container`
- `catalog`
- `asyncapi`

Existing command implementation services remain in place. Migrating from McMaster.Extensions.CommandLineUtils, redesigning commands, changing Bicep packaging, or adding new commands is out of scope.

## Target structure

```text
NimBus.CommandLine/
├── Program.cs
├── CliApplicationFactory.cs
├── CliDependencies.cs
├── Commands/
│   ├── InfraCommands.cs
│   ├── TopologyCommands.cs
│   ├── DeployCommands.cs
│   ├── SetupCommand.cs
│   ├── EndpointCommands.cs
│   ├── ContainerCommands.cs
│   ├── CatalogCommands.cs
│   └── AsyncApiCommands.cs
└── Options/
    ├── InfrastructureOptionSet.cs
    └── OptionParsers.cs
```

Names may be adjusted to match analyzer rules, but each top-level command must have one clear owner.

## Design constraints

- `Program.Main` remains the executable boundary and should only create the application, execute it, and preserve current top-level error handling.
- `CliApplicationFactory` builds the command graph. Tests may call it without invoking a process.
- `CliDependencies` supplies narrowly scoped factories for side-effecting services such as Azure CLI, infrastructure deployment, topology provisioning, and application deployment. Defaults construct the current production implementations.
- Do not introduce a general-purpose service container solely for the CLI.
- Secret-valued inputs continue to come from environment variables or secure ephemeral parameter files, never new command-line arguments.
- Preserve command option instances that must be shared with subcommands; do not accidentally register one mutable `CommandOption` under multiple parents if the library does not support it.
- Respect the repository's existing analyzer, XML documentation, and warnings-as-errors rules.

## Phase 1: characterize the command graph

1. Add `CliApplicationFactoryTests.cs` under `tests/NimBus.CommandLine.Tests`.
2. Assert the complete top-level command set and critical subcommands.
3. Assert critical option names, required flags, aliases, defaults, and help descriptions for `infra apply`, `topology apply`, `setup`, and destructive container/endpoint operations.
4. Add execution tests for invalid storage providers, invalid SQL modes, missing topology coordinates, invalid statuses, invalid dates, and invalid batch sizes.
5. Capture current exit codes and stderr/stdout placement.
6. Run these tests against the current command construction and confirm deliberate negative assertions fail before refactoring.

Avoid snapshotting the entire help output; assert stable semantic elements so harmless wrapping changes do not cause noise.

## Phase 2: introduce the application factory

1. Move application construction and global option registration from `Main` to `CliApplicationFactory.Create`.
2. Keep the existing command-registration methods temporarily in `Program` or a single transitional module.
3. Have `Main` call the factory and execute the returned application.
4. Run command-graph and existing CLI tests to prove no behavior changed.

## Phase 3: move top-level command modules mechanically

Move one group per commit in this order:

1. `asyncapi` and `catalog`
2. `endpoint` and `container`
3. `deploy` and `topology`
4. `infra` and `setup`

For each move:

- copy the existing option declarations, descriptions, validation, and callbacks without cleanup;
- expose one internal `Register` method;
- run command-graph tests plus the existing tests for that feature;
- compare help output and exit codes before doing any subsequent simplification.

This phase is file movement only. Do not combine it with option deduplication.

## Phase 4: centralize parsing and shared deployment options

1. Move `ParseStorageProvider`, `ParseSqlMode`, status-list parsing, UTC date parsing, batch-size parsing, and AsyncAPI format parsing to focused parsers with table-driven tests.
2. Introduce `InfrastructureOptionSet` to bind the options common to `infra apply` and `setup`.
3. Convert bound command options to the existing immutable option records only after validation succeeds.
4. Preserve differences between `infra apply` and `setup`; common binding must not erase command-specific defaults or identity bootstrap options.
5. Remove duplicate parsing only after equivalent-path tests pass for both commands.

## Phase 5: inject side-effecting dependencies

1. Add default factories in `CliDependencies` for `AzureCliRunner`, `InfrastructureDeployer`, `ServiceBusTopologyProvisioner`, `EndpointContainerProvisioner`, and `AppDeploymentService`.
2. Pass only the factories each command module needs.
3. Add focused tests that execute command callbacks with recording fakes and assert translated option records and operation ordering.
4. Prove `setup` still runs infrastructure, topology, Cosmos endpoint-container provisioning when applicable, and application deployment in the established order.
5. Preserve cancellation-token propagation through every asynchronous callback.

## Verification

After each command-group move:

```powershell
dotnet test tests/NimBus.CommandLine.Tests/NimBus.CommandLine.Tests.csproj -c Release
dotnet build src/NimBus.CommandLine/NimBus.CommandLine.csproj -c Release
```

Final gate:

```powershell
dotnet build src/NimBus.sln -c Release
dotnet test src/NimBus.sln -c Release --no-build
dotnet run --project src/NimBus.CommandLine -- --help
dotnet run --project src/NimBus.CommandLine -- infra apply --help
dotnet run --project src/NimBus.CommandLine -- topology apply --help
dotnet run --project src/NimBus.CommandLine -- setup --help
```

The manual smoke commands must not contact Azure because only help paths are exercised.

## Proposed pull requests

1. `test(cli): characterize command graph and validation`
2. `refactor(cli): extract command registration modules`
3. `refactor(cli): centralize deployment option binding`
4. `refactor(cli): inject command execution dependencies`

## Exit criteria

- `Program.cs` is a minimal executable entry point.
- Every top-level command has one command-registration owner.
- Shared option parsing is tested once and reused without changing command-specific behavior.
- Command tests can execute callbacks without Azure or Cosmos access.
- Help, validation, secret handling, operation ordering, cancellation, and exit codes remain compatible.
- CLI tests and full solution Release verification pass.
