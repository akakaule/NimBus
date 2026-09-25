# Plan — Spec 034 Phase 1: private networking mode

Spec: `docs/spec/034-private-networking/spec.md` (§5, §5.13, §6, §7).
Baseline: master `2ffb914b`. Phase 0 (Entra query auth) already shipped in `fd2a578a`.

## Decisions adopted for Phase 1

The spec's recommendations for its open decisions (§12):

- Bring-your-own subnets only. No NimBus-created VNet (Phase 3).
- `--private-dns` is required in private mode. There is no default.
- `--monitor-private-link` is required in private mode. Phase 1 accepts only `none`; `existing`
  and `create` arrive with Phase 2.
- Elastic Premium is fully supported alongside Flex Consumption.
- Service Bus tier change for existing environments uses a new namespace name (option A), so
  Phase 1 ships `--service-bus-namespace-name`.

## Facts settled before starting (Bicep 0.46.1 type check)

- `outboundVnetRouting` is not in `Microsoft.Web/sites@2024-04-01`. It is in `2024-11-01`, which
  also accepts `contentShareTraffic`. The EP and WebApp templates move to `2024-11-01`. Flex keeps
  `2024-04-01` and gets no routing property, because it routes all traffic natively.
- `privateDnsZones/virtualNetworkLinks@2024-06-01` accepts `resolutionPolicy: 'NxDomainRedirect'`.
- `Microsoft.ServiceBus/namespaces@2024-01-01` accepts `publicNetworkAccess`,
  `premiumMessagingPartitions` and `minimumTlsVersion`. `Microsoft.Storage/storageAccounts@2023-05-01`
  accepts `publicNetworkAccess` and `networkAcls`.

## Slices (each one shippable, tests first where there is logic)

1. **Bicep foundation.**
   - New `templates/privateEndpoint.bicep` and `templates/privateDnsZones.bicep`.
   - Network parameters and a derived `networkState` (`public`, `private-transition`, `private`)
     in both entry templates.
   - Per-state access settings on Service Bus (Premium), Cosmos DB, SQL, storage and the sites.
   - VNet integration: `outboundVnetRouting.allTraffic` on EP and the WebApp; native on Flex.
   - EP runtime scale monitoring, and the content share declared for EP.
   - Ordering through `dependsOn`, and `nimbus-deployment` tags on network resources.
   - Template string tests in `BicepTemplateProviderTests` style.
   - `az bicep build` and lint on both entry templates, with public-mode and private-mode
     parameter files.
2. **CLI surface.**
   - Options on `infra apply` and `setup`, and new `InfrastructureOptions` members.
   - Validation before side effects: subnet existence, region and delegation;
     management SKU; providers.
   - Parameter pass-through.
   - Service Bus SKU pin: refuse private mode on an existing Standard namespace.
   - `--service-bus-namespace-name` on `infra apply`, `topology apply` and `setup`. Not on
     `deploy apps`: it never addresses the namespace. The spec listed it by mistake.
   - Interim guard until slice 3: without `--network-mode`, a namespace whose public access
     is Disabled stops the run instead of silently reopening it.
3. **Recorded intent (§5.13).** Decided 2026-09-24: record the full setup, not only the mode.
   - Tags on the resource group, one per setting: `nimbus-network-mode`, the three subnet
     ids, the DNS mode and zone scope, `nimbus-network-dns-link-vnet-<n>` per linked VNet,
     the monitoring choice, and `nimbus-service-bus-namespace` for the override.
   - Written after validation and before deploying, so an interrupted run converges and a
     failed validation never records anything.
   - A deployment that never used private mode or an override gets no tags.
   - Precedence per setting: explicit flag, then the tag, then observed state, then public.
     `--network-mode private` alone ends a recorded transition; `--network-mode public`
     records public and deletes the stale network tags. Customer tags are never touched.
   - Stop when the namespace is more locked down than the record.
   - `--skip-transition` guards a direct public → private switch of an existing deployment.
   - `nb topology apply` follows the recorded namespace override.
4. **Pre-flight DNS check (§5.7).**
   - Expected addresses come from each private endpoint's network interface
     (`ipConfigurations[].privateLinkConnectionProperties.fqdns` plus its private IP),
     not from `customDnsConfigs`. The NIC carries them in every DNS mode.
   - Retry with backoff (10 s doubling to 60 s) for up to `--dns-wait` minutes, default 10,
     0 to 120.
   - Three diagnoses: public address, no record, stale record.
   - Placement: inside the topology step (Service Bus endpoint) and the app deployment step
     (the scm endpoints of the apps being deployed), so `setup` checks after its infra step
     by construction. Also in `infra apply`, before locking on the transition → private
     step, as a hard failure that records nothing.
   - The recorded state decides severity: public skips, transition warns, private fails.
     The state read is soft (a failed read means public), so public deployments see no new
     failure mode.
5. **Rollback cleanup (§5.13).**
   - Runs after both public deployments have reopened access, on every public run that has a
     record, so an interrupted cleanup continues on the next run.
   - Remove VNet integration from each app that has it.
   - Delete the private endpoints carrying `nimbus-deployment=<solution>-<env>`. The tag filter
     runs server-side.
   - Delete the NimBus-tagged VNet links, then each NimBus-tagged zone that has no other
     links left. Ownership, not the recorded DNS mode, decides: a completed switch to
     public no longer records the mode.
   - With external DNS, remind the customer to remove manual records.
   - Delete the SQL allow-all rule after the core deployment when a NimBus-provisioned server
     moves to `private`. It is kept during the transition.
   - Every az command and flag used in slices 2 to 5 was checked against the local az CLI's
     help.
6. **Pipelines and docs.**
   - `deploy.yml`: `runs-on` input and environment variables for the network options.
   - ADO pipeline: `pool` parameter.
   - `docs/private-networking.md`, plus updates to `deployment.md`, `azure-requirements.md` and
     `cli.md`.
   - `deploy.core.private.example.bicepparam` and ADR-016.

## Verification per slice

- `dotnet build src/NimBus.sln -c Release` and `dotnet test tests/NimBus.CommandLine.Tests`.
- `az bicep build` on every entry template.
- Live Azure checks (what-if, a private deployment, the transition cycle) need a sandbox
  subscription and an in-network runner. Each slice reports them as not run until they happen.
