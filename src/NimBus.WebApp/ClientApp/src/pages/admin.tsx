import Page from "components/page";
import { Tabs, TabList, Tab, TabPanels, TabPanel } from "components/ui/tabs";
import TopologyAudit from "components/admin/topology-audit";
import Operations from "components/admin/operations";
import DevTools from "components/dev/dev-tools";
import useDevMode from "hooks/use-dev-mode";

export default function Admin() {
  const isDev = useDevMode();
  return (
    <Page title="Admin">
      <Tabs defaultIndex={0} isLazy={true} className="w-full">
        <TabList>
          <Tab index={0}>Topology</Tab>
          <Tab index={1}>Operations</Tab>
          {isDev && <Tab index={2}>Dev Tools</Tab>}
        </TabList>
        <TabPanels>
          <TabPanel index={0} className="p-6">
            <TopologyAudit />
          </TabPanel>
          <TabPanel index={1} className="p-6">
            <Operations />
          </TabPanel>
          {isDev && (
            <TabPanel index={2} className="p-6">
              <DevTools />
            </TabPanel>
          )}
        </TabPanels>
      </Tabs>
    </Page>
  );
}
