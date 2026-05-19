import * as React from "react";
const { useEffect, useState } = React;

import * as api from "api-client";
import { useParams } from "react-router-dom";
import Page from "components/page";
import EventsPanel from "components/endpoint-details/events-panel";
import EventTypesPanel from "components/endpoint-details/event-types-panel";
import NotFoundPage from "components/not-found-page";
import { cn } from "lib/utils";

export interface ComposeNewResponse {
  hasError: boolean;
  responseString: string;
}

type EndpointDetailsProps = {
  endpointState?: api.EndpointStatus;
};

type DetailTab = "messages" | "event-types";

const EndpointDetails = (props: EndpointDetailsProps) => {
  const client = new api.Client(api.CookieAuth());
  const params = useParams();
  const endpointId = params.id!;

  const [endpointIsInvalid, setEndpointIsInvalid] = useState<boolean>(false);
  const [activeTab, setActiveTab] = useState<DetailTab>("messages");

  // Validate endpoint exists
  useEffect(() => {
    const fetchData = async () => {
      try {
        if (!props.endpointState) {
          await client.getApiEndpointstatusStatusEndpointName(endpointId);
        }
      } catch (e) {
        console.log("Failed to load endpoint details");
        if (e instanceof api.SwaggerException) {
          if (e.status === 404) {
            setEndpointIsInvalid(true);
          }
        }
      }
    };

    fetchData();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  if (endpointIsInvalid) {
    return (
      <NotFoundPage errMsg={"No endpoint found with name: " + endpointId} />
    );
  }

  return (
    <Page title={endpointId} subtitle="Endpoint details">
      <div className="flex flex-col gap-4 w-full">
        <TabStrip activeTab={activeTab} onChange={setActiveTab} />
        {activeTab === "messages" ? (
          <EventsPanel endpointId={endpointId} />
        ) : (
          <EventTypesPanel endpointId={endpointId} />
        )}
      </div>
    </Page>
  );
};

interface TabStripProps {
  activeTab: DetailTab;
  onChange: (tab: DetailTab) => void;
}

const tabs: { id: DetailTab; label: string }[] = [
  { id: "messages", label: "Messages" },
  { id: "event-types", label: "Event Types" },
];

const TabStrip: React.FC<TabStripProps> = ({ activeTab, onChange }) => (
  <div
    role="tablist"
    className="flex gap-1 border-b border-border -mb-1"
  >
    {tabs.map((t) => {
      const isActive = activeTab === t.id;
      return (
        <button
          key={t.id}
          role="tab"
          type="button"
          aria-selected={isActive}
          onClick={() => onChange(t.id)}
          className={cn(
            "bg-transparent border-0 px-[18px] py-[11px] text-[13.5px] font-semibold cursor-pointer",
            "border-b-2 border-transparent -mb-px inline-flex items-center gap-2",
            "transition-colors focus:outline-none focus:ring-2 focus:ring-primary focus:ring-inset",
            isActive
              ? "text-primary-600 border-b-primary bg-gradient-to-b from-transparent to-primary-tint"
              : "text-ink-2 hover:text-ink",
          )}
        >
          {t.label}
        </button>
      );
    })}
  </div>
);

export default EndpointDetails;
