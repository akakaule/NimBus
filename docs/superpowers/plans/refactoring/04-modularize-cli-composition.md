# Plan 4: Modularize CLI composition

## Goal

Centralize duplicated CLI parsing, make command execution testable, and relocate the eight existing top-level registration methods out of `Program.cs` without changing command names, flags, defaults, help text, environment-variable secret handling, or exit codes.

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

## Preconditions

- Begin from a baseline containing the ADR-015 deployment-distribution work (`d9b3108`) and its documentation follow-up (`51568d0`). That work changed `Program.cs` and adjacent CLI services and must not be mixed with this refactor.
- Start with a clean `Program.cs` in a dedicated branch or worktree. If feature work is again in flight on the file, stop and land or isolate it first.
- Preserve all packaged-template and repository-free deployment behavior introduced by ADR-015.

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

Names may be adjusted to match analyzer rules. `Program.cs` already contains one named `Configure*` method per top-level command; the `Commands/` files relocate those existing owners rather than inventing new boundaries.

## Design constraints

- `Program.Main` remains the executable boundary and should only create the application, execute it, and preserve current top-level error handling.
- `CliApplicationFactory` builds the command graph. `Create` returns an owned `CommandLineApplication`, which is disposable: `Main` must retain a `using` declaration and tests must dispose every created application.
- `CliDependencies` supplies narrowly scoped factories for side-effecting services such as Azure CLI, infrastructure deployment, topology provisioning, and application deployment. Defaults construct the current production implementations.
- Do not introduce a general-purpose service container solely for the CLI.
- Secret-valued inputs continue to come from environment variables or secure ephemeral parameter files, never new command-line arguments.
- Preserve the existing identity of shared `CommandOption` instances such as `sbConnectionString` and `dbConnectionString`. Command modules receive and register those exact instances; they must not construct per-module copies because callbacks read the shared parsed value.
- Respect the repository's existing analyzer, XML documentation, and warnings-as-errors rules.

## Phase 1: characterize the command graph

1. Add `CliApplicationFactoryTests.cs` under `tests/NimBus.CommandLine.Tests`.
2. Assert the complete top-level command set and critical subcommands.
3. Assert critical option names, required flags, aliases, defaults, and help descriptions for `infra apply`, `topology apply`, `setup`, and destructive container/endpoint operations.
4. Assert reference identity for the shared Service Bus and database connection options across the command groups that reuse them.
5. Add execution tests for invalid storage providers, invalid SQL modes, missing topology coordinates, invalid statuses, invalid dates, and invalid batch sizes.
6. Capture current exit codes and stderr/stdout placement.
7. Run these tests against the current command construction and confirm deliberate negative assertions fail before refactoring.

Avoid snapshotting the entire help output; assert stable semantic elements so harmless wrapping changes do not cause noise.

## Phase 2: introduce the application factory

1. Move application construction and global option registration from `Main` to `CliApplicationFactory.Create`.
2. Keep the existing command-registration methods temporarily in `Program` or a single transitional module.
3. Have `Main` retain disposal ownership with `using var app = CliApplicationFactory.Create(...)`, then execute it.
4. Make every test use `using` for the returned application so CA2000 and disposable-lifetime analyzers remain satisfied.
5. Run command-graph and existing CLI tests to prove no behavior changed.

## Phase 3: centralize parsing and shared deployment options

1. Move `ParseStorageProvider`, `ParseSqlMode`, status-list parsing, UTC date parsing, batch-size parsing, and AsyncAPI format parsing to focused parsers with table-driven tests.
2. Introduce `InfrastructureOptionSet` to bind the options common to `infra apply` and `setup`.
3. Convert bound command options to the existing immutable option records only after validation succeeds.
4. Preserve differences between `infra apply` and `setup`; common binding must not erase command-specific defaults, packaged-template behavior, or identity bootstrap options.
5. Remove duplicate parsing only after equivalent-path tests pass for both commands.

## Phase 4: relocate the existing command owners

`Program.cs` already owns the eight command groups through eight named methods: `ConfigureInfraCommands`, `ConfigureTopologyCommands`, `ConfigureDeployCommands`, `ConfigureSetupCommand`, `ConfigureEndpointCommands`, `ConfigureContainerCommands`, `ConfigureCatalogCommands`, and `ConfigureAsyncApiCommands`.

1. Move those existing methods into the corresponding `Commands/` files in one mechanical pull request, or two commits only if review size requires it.
2. Expose one internal `Register` method per module.
3. Pass the shared option objects created by `CliApplicationFactory`; do not recreate them inside modules.
4. Make no option, validation, callback, or help-text edits during the move.
5. Run the command-graph tests and CLI Release build once after the relocation.

This phase is relocation only. The semantic characterization and parser tests from Phases 1 and 3 are the proof; do not spend review attention on repeated per-group help comparisons for unchanged method bodies.

## Phase 5: inject side-effecting dependencies

1. Add default factories in `CliDependencies` for `AzureCliRunner`, `InfrastructureDeployer`, `ServiceBusTopologyProvisioner`, `EndpointContainerProvisioner`, and `AppDeploymentService`.
2. Pass only the factories each command module needs.
3. Add focused tests that execute command callbacks with recording fakes and assert translated option records and operation ordering.
4. Prove `setup` still runs infrastructure, topology, Cosmos endpoint-container provisioning when applicable, and application deployment in the established order.
5. Preserve cancellation-token propagation through every asynchronous callback.

## Verification

After each behavioral phase and after the module relocation:

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
2. `refactor(cli): add owned application factory`
3. `refactor(cli): centralize deployment option binding`
4. `refactor(cli): relocate command registration modules`
5. `refactor(cli): inject command execution dependencies`

## Exit criteria

- `Program.cs` is a minimal executable entry point.
- Every existing top-level command owner has moved intact to a focused module.
- Shared option parsing is tested once and reused without changing command-specific behavior.
- Shared connection options retain reference identity across command modules.
- Application-factory disposal ownership is explicit in production and tests.
- Command tests can execute callbacks without Azure or Cosmos access.
- Help, validation, secret handling, operation ordering, cancellation, and exit codes remain compatible.
- CLI tests and full solution Release verification pass.
