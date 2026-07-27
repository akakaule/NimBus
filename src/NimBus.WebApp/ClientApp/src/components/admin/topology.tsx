import { Accordion } from "components/ui/accordion";
import { OperationGroup } from "./operation-group";
import AsyncApiExport from "./asyncapi-export";
import TopologyAudit from "./topology-audit";

/**
 * Admin → Topology tab. Mirrors the Operations tab's blast-radius grouping
 * (shared OperationGroup accordion) so both tabs read identically: coloured
 * rail, icon badge, operation count, and a right-aligned safety caption.
 */
export default function Topology() {
  return (
    <div className="w-full">
      <Accordion allowMultiple={true} defaultExpandedItems={["catalog-export"]}>
        <OperationGroup
          id="catalog-export"
          tone="info"
          icon="⇩"
          title="Catalog Export"
          count={1}
          caption="Read-only · YAML / JSON"
          description="Download the full platform topology as an AsyncAPI 3.0 document (channels, operations, and Service Bus routing extensions)."
        >
          <AsyncApiExport />
        </OperationGroup>

        <OperationGroup
          id="topology-audit"
          tone="warning"
          icon="⌕"
          title="Topology Audit"
          count={2}
          caption="Audit read-only · cleanup gated"
          description="Compare an endpoint's live Service Bus subscriptions and rules against the declared catalog. Removing deprecated items changes the namespace and requires typed confirmation."
        >
          <TopologyAudit />
        </OperationGroup>
      </Accordion>
    </div>
  );
}
