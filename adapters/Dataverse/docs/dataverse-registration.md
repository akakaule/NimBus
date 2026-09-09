# Register Dataverse ingress

1. Use a designated non-production Dataverse organization and its real OrganizationId. Deploy preview infrastructure disabled. The supplied template assumes existing Service Bus namespaces and NimBus publisher topic in the target resource group.
2. Add the adapter endpoint/contracts to the customer NimBus Platform catalog and provision the ordinary subscriber topology. Enable and document duplicate detection on the publisher topic where appropriate; its bounded window is not an exactly-once guarantee.
3. Obtain the ingress queue's `dataverse-send` SAS credentials using the customer's secure administrative process. The policy grants Send only. Do not grant Dataverse the namespace root key or store it in parameter files.
4. In the Plug-in Registration Tool register a Service Endpoint pointing to that queue, with Queue contract and JSON message format.
5. Register async PostOperation (stage 40, mode 1) steps for Create, Update, Delete on each allowed table. Configure Update filtering columns. Filtering columns indicate presence in the request, not necessarily a changed value.
6. Configure allowed column projections through `Dataverse__Tables__<logical-name>__<index>`. Set OrganizationId to the source organization GUID, not its URL or tenant ID.
7. If setting PreImageAlias, register that alias for Update/Delete. If setting PostImageAlias, register it for Create/Update. Aliases are instance-wide; missing configured images are permanent failures. Configure required image columns explicitly and keep contexts small.
8. Capture representative contexts and repeat deliveries in the test organization. Verify OperationId and OwningExtension.Id identify the same source occurrence across source job retries; distinct updates must remain distinct. This is a release gate, not an assumed platform guarantee.
9. Enable the trigger only in the designated test environment and perform controlled Create/Update/Delete operations. Correlate System Jobs, queue delivery, adapter logs, NimBus session/event and subscriber outcome.

Rotation: update the Service Endpoint's sender credential using the alternate SAS key, prove delivery, then rotate the retired key. The Function App uses managed identity and is not changed by source SAS rotation.

Rollback: disable Dataverse steps and the Function trigger; retain the queue/DLQ and deploy the previously verified bundle. Do not delete queues to roll back application code. Diagnose source-send failures in Dataverse System Jobs, independently from ingress failures.

[Microsoft registration guide](https://learn.microsoft.com/en-us/power-apps/developer/data-platform/walkthrough-configure-azure-sas-integration)
