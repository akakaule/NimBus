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
3. **Recorded intent (§5.13).**
   - The resource-group tag `nimbus-network-mode`, written before deploying.
   - Precedence: flag, then tag, then observed state, then public.
   - Stop when the observed state is more locked down than the tag.
   - `--skip-transition` guard for a direct public → private switch.
4. **Pre-flight DNS check (§5.7).**
   - Compare each resolved address with the private endpoint's `customDnsConfigs`.
   - Retry with backoff for up to `--dns-wait` minutes.
   - Three diagnoses: public address, no record, stale record.
   - Placement: after infra in `setup`; at the start of `topology apply` and `deploy apps`;
     before locking on the transition → private step.
5. **Rollback cleanup (§5.13).**
   - Reopen public access first.
   - Remove VNet integration from both apps.
   - Delete NimBus-owned private endpoints, then, in `create` mode only, the zones and links.
   - Delete the SQL allow-all rule on the switch to `private`.
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
