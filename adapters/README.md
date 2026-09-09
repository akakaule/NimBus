# NimBus system adapters

Optional platform extensions that turn third-party system messages into NimBus events. Each adapter owns its packages, host, tests, deployment and compatibility documentation. Core must not depend on an adapter.

| Adapter | Default host | Status |
| --- | --- | --- |
| [Dataverse](Dataverse/README.md) | .NET isolated Azure Function App | Preview; external integration qualification pending |

Third-party implementations may use public NimBus SDK packages from another repository. Keeping first-party source here does not make it part of every platform deployment.
