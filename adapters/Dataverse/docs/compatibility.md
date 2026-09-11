# Compatibility and release status

Status: preview implementation, not production-qualified.

- Runtime: .NET 10, Functions v4 isolated worker. Uses the same Worker 2.52.0 / Worker.Sdk 2.1.0 / Service Bus extension 5.24.0 family as existing repository samples; a future Azure.Functions.Sdk migration is independent work.
- Input: JSON execution contexts for async PostOperation Create/Update/Delete, key/value-array parameter and image collections. No binary/XML deserialization.
- Attributes: JSON scalars; Money, OptionSetValue, EntityReference with the documented XRM contract namespace; arrays of OptionSetValue. Source date strings, including DataContract dates, remain strings. Other complex types dead-letter when selected. No inferred current-state lookup.
- Projection: explicit allowed tables/columns. Unsupported operations and unconfigured tables dead-letter. No silent exclusions are implemented.
- Identity: organization + owning step + OperationId + table + record + output contract type. Synthetic tests cover stable redelivery and distinct inputs. Real Dataverse job retries/reposts must verify this assumption before a production release.
- Sessions: organization/table/record; SHA-256 shortening when the key exceeds 128 UTF-8 bytes. Sessions order broker arrivals, not Dataverse commits.
- Contracts: DataverseRecordCreatedV1, DataverseRecordUpdatedV1, DataverseRecordDeletedV1. Type names are the actual NimBus catalog event IDs.
- Published NimBus version range: not established. CI accepts an explicit version for package-mode builds; a passing build is required before advertising that version. Default development builds reference the current repository SDK.
- No SQL/outbox, source version reconstruction, custom mapping plug-in loader or topic ingress variant.

Before release: real Dataverse fixtures and stable identity proof; Azure Flex/identity/network/scale-out smoke; real NimBus subscriber/Resolver outcome; duplicate-detection/replay tests; packed consumer compatibility against a published SDK; operational owner and retention/SLA decisions. No release tag or public package has been published.
