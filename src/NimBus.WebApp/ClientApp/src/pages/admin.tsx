import Page from "components/page";
import { Tabs, TabList, Tab, TabPanels, TabPanel } from "components/ui/tabs";
import TopologyAudit from "components/admin/topology-audit";
import BulkOperations from "components/admin/bulk-operations";
import SessionManagement from "components/admin/session-management";
import AdvancedOperations from "components/admin/advanced-operations";
import DevTools from "components/dev/dev-tools";
import useDevMode from "hooks/use-dev-mode";

export default function Admin() {
  const isDev = useDevMode();
  return (
    <Page title="Admin">
      <Tabs defaultIndex={0} isLazy={true} className="w-full">
        <TabList>
          <Tab index={0}>Topology Audit</Tab>
          <Tab index={1}>Bulk Operations</Tab>
          <Tab index={2}>Session Management</Tab>
          <Tab index={3}>Advanced Operations</Tab>
          {isDev && <Tab index={4}>Dev Tools</Tab>}
        </TabList>
        <TabPanels>
          <TabPanel index={0} className="p-6">
            <TopologyAudit />
          </TabPanel>
          <TabPanel index={1} className="p-6">
            <BulkOperations />
          </TabPanel>
          <TabPanel index={2} className="p-6">
            <SessionManagement />
          </TabPanel>
          <TabPanel index={3} className="p-6">
            <AdvancedOperations />
          </TabPanel>
          {isDev && (
            <TabPanel index={4} className="p-6">
              <DevTools />
            </TabPanel>
          )}
        </TabPanels>
      </Tabs>
    </Page>
  );
}
