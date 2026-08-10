import Page from "components/page";
import { Tabs, TabList, Tab, TabPanels, TabPanel } from "components/ui/tabs";
import Topology from "components/admin/topology";
import Operations from "components/admin/operations";
import SubscriptionManager from "components/admin/subscription-manager";
import DevTools from "components/dev/dev-tools";
import useDevMode from "hooks/use-dev-mode";

export default function Admin() {
  const isDev = useDevMode();
  return (
    <Page
      title="Admin"
      subtitle="Topology, bulk operations, and developer tools. Some actions are irreversible."
    >
      <Tabs defaultIndex={0} isLazy={true} className="w-full">
        <TabList>
          <Tab index={0}>Topology</Tab>
          <Tab index={1}>Operations</Tab>
          <Tab index={2}>Subscriptions</Tab>
          {isDev && <Tab index={3}>Dev Tools</Tab>}
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
          {isDev && (
            <TabPanel index={3} className="p-6">
              <DevTools />
            </TabPanel>
          )}
        </TabPanels>
      </Tabs>
    </Page>
  );
}
