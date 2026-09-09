# Operations

## Failure domains

Dataverse System Jobs report failure before enqueue. Functions/ingress queue report parsing or publishing failures. Resolver/WebApp report downstream handling after NimBus publication. A parse failure is not a Resolver-managed event.

The runtime emits Application Insights request/dependency/exception telemetry and custom metric aggregates `dataverse.received`, `dataverse.rejected`, `dataverse.published_and_completed`. For aggregate count, use the metric sum. The last metric records successful publication followed by successful input completion; it is not a unique-message counter. Logs contain safe reason codes, never source attributes. Source identity is carried in the event; avoid enabling verbose payload logging in supporting tools.

Configure customer alerts for queue oldest-message age, active/DLQ counts, Function exceptions and rejection rate. Dashboard URLs, alert thresholds, retention and incident ownership remain customer decisions; no alert rules are provisioned by this preview.

## Disposition

- Transient send failure, cancellation, lock loss and completion failure: propagate; input remains eligible for broker redelivery. Queue MaxDeliveryCount is 10 in the template.
- InvalidJson/InvalidUtf8/InvalidValue: inspect a restricted copy of the source message and correct source/configuration.
- TruncatedContext/ContextTooLarge/OutputTooLarge: reduce images/selected data. Missing data is not fabricated.
- UnexpectedOrganization/TableNotAllowed/OperationNotSupported/UnsupportedExecutionStage: verify source registration and explicit allowlist.
- MissingOrInvalidIdentity/TargetIdentityMismatch/ImageIdentityMismatch: verify source context identity; do not generate a random replacement to bypass validation.
- MissingTarget/MissingCollection/MissingRequiredImage/InvalidCollection/DuplicateAttribute/TrailingContent/UnsupportedAttributeType/InvalidMoney/InvalidChoice/InvalidLookup: correct the input contract, supported type selection or image setup.

Permanent failures dead-letter with a reason code and a fixed safe description. They are not automatically resubmitted. No host-level exception details are copied into the DLQ description.

## Controlled replay

After correcting the cause, use your existing Service Bus administration tooling to copy selected DLQ messages back to the ingress queue, retaining body and relevant application properties. Remove a truncation flag only by reconstructing a verified complete source event, not by suppressing the validation. Retain source occurrence identity. Complete DLQ originals only after enqueue succeeds. Keep a replay audit and expect duplicates if that acknowledgement is lost. Do not replay the whole DLQ indiscriminately.

The adapter publishes before completing input. A crash between those operations can publish twice. Broker duplicate detection helps only inside its configured window; downstream consumers must be idempotent beyond it. Reordering before the NimBus session is possible. Replay cannot restore source commit order.

## Local verification versus qualification

Local tests use synthetic contexts and a real NimBus PublisherClient with a recording sender. They prove mapping, identity construction and settlement ordering, not Dataverse retry semantics, cloud authorization or Service Bus delivery. Run the external acceptance gate before any production enablement.
