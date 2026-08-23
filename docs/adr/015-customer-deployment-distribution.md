# ADR-015: Ship Customer Deployments as a Starter Repo, Then as Packaged Deployables

## Status
Proposed (2026-08)

## Context

A NimBus deployment consists of three layers (see [deployment.md](../deployment.md)): Azure resources from `deploy/bicep/`, the Service Bus topology applied by `nb topology apply`, and the application code — `src/NimBus.Resolver` and `src/NimBus.WebApp` — published and zip-deployed by `nb deploy apps`.

Only two of the pieces those layers need are distributed. `Akaule.NimBus.*` on nuget.org ships the libraries and the `nb` CLI; the CLI package embeds nothing but a README, and `CommandSupport.cs` fails with *"Could not locate the NimBus repository root"* when it cannot find a source tree. The bicep templates and both deployable apps are read from that tree. **Every deployment therefore requires a copy of the NimBus repository at a revision matching the CLI version.**

That is invisible while NimBus is deployed by the team that develops it. It becomes the central question once customers deploy into their own Azure DevOps organisation and Azure subscription with their own service connection, because it forces two things on them: a mechanism for obtaining and pinning NimBus source, and — since the source is the whole repository — full visibility of the implementation and test suite. The second point is a licensing decision as much as a packaging one: as long as deployment requires the repo, NimBus cannot be shipped as anything but source-available.

The repository already ships `pipelines/azure-pipelines-deploy.yml` (deployment.md "Path 3"), but it assumes the pipeline runs *inside* a NimBus clone, which is not how a customer's own deployment repository is shaped. The `NimBusDemo` repository is the first real instance of the customer shape: its own pipelines, its own parameters, and NimBus source acquired as a pinned git submodule with a guard step comparing the installed CLI version against the submodule's nearest release tag.

Options considered:

1. **Starter deployment repository** — a template the customer copies into their ADO project, carrying pipelines and parameters, obtaining NimBus source by submodule, repo resource, or clone. Proven (NimBusDemo runs this way), transparent, and the customer owns their upgrade cadence. Does not remove the source requirement.
2. **Package the deployables** — embed the bicep in the CLI package and publish per-release application zips, so `nb` needs no source tree. Removes the requirement outright; requires CI and CLI work.
3. **`nb init` scaffolder** — a CLI command generating the pipeline YAML and parameter files into an empty repository. Attractive, but only after option 2; scaffolding a pipeline that still needs a source clone reproduces the problem it is meant to hide.
4. **Customer imports the whole NimBus repository into their ADO org** — one artifact, no submodule, and the shipped Path 3 pipeline works unchanged. But the customer now owns a fork that drifts, re-syncing is manual forever, and they receive the tests and samples they did not ask for.
5. **Azure Marketplace / Managed Application, or an `azd` template** — a portal-driven install. Justified only behind a commercial offer, and still dependent on option 2 for its artifacts.

## Decision

Adopt options 1 and 2 in sequence, and treat option 3 as a consequence of option 2.

**Phase 1 — the starter deployment repository is the supported onboarding path.** Generalise the NimBusDemo shape into a published template. It already encodes what onboarding gets wrong: parameterised pipelines, a `<clear/>` NuGet.config so restore does not depend on the customer's internal feeds, and an explicit split between the infrastructure and application pipelines.

Two changes for external customers:

- **No git submodule.** A GitHub submodule fetched from an ADO pipeline means credentials for private repositories and egress rules on self-hosted agents. Prefer an ADO `resources: repositories:` entry pinned to a tag (the customer imports NimBus into their own organisation once), or a pipeline-time `git clone --depth 1 --branch vX.Y.Z` where agents have GitHub access.
- **Pin the CLI version explicitly** (`dotnet tool install --version X.Y.Z`) rather than installing latest and guarding afterwards. NimBusDemo's guard step exists because a floating CLI can outrun a pinned source tree; for a customer, a deployment that fails because NimBus published a release is the wrong first impression. Pin both revisions and make upgrades a deliberate change.

**Phase 2 — remove the source requirement.** Make `nb` self-sufficient:

- **Bicep templates ship inside the CLI package** as embedded resources. They are small text files, and this alone frees layer 1.
- **Prebuilt application zips are published per release** (GitHub Release assets or an OCI artifact) and downloaded by the CLI for its own version. The customer's agents then need neither Node 22 nor a .NET SDK, and they deploy the exact artifact that was tested rather than rebuilding it on an agent NimBus has never seen.
- `--repo-root` remains, demoted to a developer override.

Once Phase 2 lands, onboarding is: install the tool, create a service connection, run `nb setup` — and `nb init` becomes worth building, because the generated pipeline can ship with the CLI version it belongs to instead of living in a separately maintained template.

## Consequences

- Customer onboarding has one supported path. `pipelines/azure-pipelines-deploy.yml` and the starter repository would otherwise drift; the in-repo pipeline should be documented as the "deploy from a clone" developer path, or retired.
- Phase 2 changes the release process: CI must build and publish the Resolver and WebApp zips for every tag, and the CLI must resolve artifacts for its own version. A CLI that cannot find its matching artifacts must fail clearly rather than falling back to a source tree that may be a different revision.
- Phase 2 is a prerequisite for licensing NimBus as anything other than source-available, and for options 3 and 5. Phase 1 is comfortable indefinitely if NimBus stays open source.
- Deployments become faster and reproducible: no `npm install` or `dotnet publish` on the customer's agent, and the deployed bits are identical to the tested ones.
- Existing deployment repositories (NimBusDemo) keep working — `--repo-root` is retained — and can migrate at their own pace.
- The operational prerequisites stay the customer's responsibility and must be documented up front, because they are where onboarding actually fails: the service connection needs **Owner**, or **Contributor + Role Based Access Control Administrator**, since the bicep creates role assignments; resource provider registration (`Microsoft.ServiceBus`, `Web`, `Storage`, `Insights`, `DocumentDB`, `EventGrid`) needs subscription-level rights that usually belong to another team; and Cosmos container creation over Entra data-plane tokens requires the fix in `13a4f50`, which sets the minimum supported version for new customers.
- A preflight command (`nb doctor`) checking az version and login, RBAC on the target resource group, provider registrations, and toolchain presence would convert most of the above from a mid-deployment failure into a checklist. Worth building alongside Phase 1.
