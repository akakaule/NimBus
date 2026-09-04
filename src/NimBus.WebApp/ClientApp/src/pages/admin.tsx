import Page from "components/page";
import { Tabs, TabList, Tab, TabPanels, TabPanel } from "components/ui/tabs";
import Topology from "components/admin/topology";
import Operations from "components/admin/operations";
import SubscriptionManager from "components/admin/subscription-manager";
import Health from "components/admin/health";

export default function Admin() {
  return (
    <Page
      title="Admin"
      subtitle="Topology and bulk operations. Some actions are irreversible."
    >
      <Tabs defaultIndex={0} isLazy={true} className="w-full">
        <TabList>
          <Tab index={0}>Topology</Tab>
          <Tab index={1}>Operations</Tab>
          <Tab index={2}>Subscriptions</Tab>
          <Tab index={3}>Health</Tab>
        </TabList>
        <TabPanels>
          <TabPanel index={0} className="p-6">
            <Topology />
          </TabPanel>
          <TabPanel index={1} className="p-6">
            <Operations />
          </TabPanel>
          <TabPanel index={2} className="p-6">
            <SubscriptionManager />
          </TabPanel>
          <TabPanel index={3} className="p-6">
            <Health />
          </TabPanel>
        </TabPanels>
      </Tabs>
    </Page>
  );
}
