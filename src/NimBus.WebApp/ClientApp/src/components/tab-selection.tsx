import { useLocation } from "react-router-dom";
import { Tabs, TabList, Tab, TabPanels, TabPanel } from "components/ui/tabs";

export interface ITab {
  name: string;
  content: JSX.Element;
  isEnabled: boolean;
}

interface ITabSelectionsProps {
  tabs: ITab[];
}

const TabSelection: React.FunctionComponent<ITabSelectionsProps> = (props) => {
  const locationState = useLocation().state as { tabIndex: string };
  const index = locationState ? parseInt(locationState.tabIndex) : 0;

  if (!props?.tabs) {
    return null;
  }

  return (
    <Tabs
      isLazy={true}
      isFitted={true}
      variant="enclosed"
      defaultIndex={isNaN(index) ? 0 : index}
      className="p-4 border border-input rounded"
    >
      <TabList className="mb-4">
        {props.tabs.map((tab, idx) => (
          <Tab key={`tab-${tab.name}`} index={idx} isDisabled={!tab.isEnabled}>
            {tab.name}
          </Tab>
        ))}
      </TabList>
      <TabPanels>
        {props.tabs.map((tab, idx) => (
          <TabPanel key={`tabpanel-${tab.name}`} index={idx}>
            {tab.content}
          </TabPanel>
        ))}
      </TabPanels>
    </Tabs>
  );
};

export default TabSelection;
