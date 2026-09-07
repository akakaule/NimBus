import * as React from "react";
const { useEffect, useMemo, useState } = React;

import * as api from "api-client";
import { useParams } from "react-router-dom";
import Page from "components/page";
import EventsPanel from "components/endpoint-details/events-panel";
import EventTypesPanel from "components/endpoint-details/event-types-panel";
import SubscriptionsTab from "components/endpoint-details/tabs/subscriptions-tab";
import AuditTab from "components/endpoint-details/tabs/audit-tab";
import NotFoundPage from "components/not-found-page";
import { cn } from "lib/utils";

export interface ComposeNewResponse {
  hasError: boolean;
  responseString: string;
}

type EndpointDetailsProps = {
  endpointState?: api.EndpointStatus;
};

type DetailTab = "messages" | "event-types" | "alerts" | "audit";

const EndpointDetails = (props: EndpointDetailsProps) => {
  const { id } = useParams();
  return <EndpointDetailsContent key={id} {...props} endpointId={id!} />;
};

// Scope panel state and pending lookups to one endpoint, including route changes.
const EndpointDetailsContent = (
  props: EndpointDetailsProps & { endpointId: string },
) => {
  const { endpointId } = props;
  const [client] = useState(() => new api.Client(api.CookieAuth()));

  const [endpointIsInvalid, setEndpointIsInvalid] = useState<boolean>(false);
  const [activeTab, setActiveTab] = useState<DetailTab>("messages");
  const [visitedTabs, setVisitedTabs] = useState<Set<DetailTab>>(
    () => new Set(["messages"]),
  );
  const [alertSubscriptions, setAlertSubscriptions] = useState<
    api.EndpointSubscription[]
  >([]);
  const [isSubscriptionTabEnabled, setIsSubscriptionTabEnabled] =
    useState(false);

  // Discover availability without mounting the Alerts panel. Reuse these rows
  // when it is first opened so discovery does not cause a second request.
  useEffect(() => {
    let active = true;
    client
      .getEndpointSubscribe(endpointId)
      .then((subscriptions) => {
        if (!active) return;
        setAlertSubscriptions(subscriptions);
        setIsSubscriptionTabEnabled(subscriptions.length > 0);
      })
      .catch(() => {
        // Match the existing behavior: an unavailable probe leaves Alerts disabled.
      });
    return () => {
      active = false;
    };
  }, [client, endpointId]);

  const selectTab = (tab: DetailTab) => {
    setVisitedTabs((visited) =>
      visited.has(tab) ? visited : new Set([...visited, tab]),
    );
    setActiveTab(tab);
  };

  // Validate endpoint exists
  useEffect(() => {
    let active = true;
    const fetchData = async () => {
      try {
        if (!props.endpointState) {
          await client.getApiEndpointstatusStatusEndpointName(endpointId);
        }
      } catch (e) {
        console.log("Failed to load endpoint details");
        if (active && e instanceof api.SwaggerException) {
          if (e.status === 404) {
            setEndpointIsInvalid(true);
          }
        }
      }
    };

    void fetchData();
    return () => {
      active = false;
    };
  }, [client, endpointId, props.endpointState]);

  const tabs = useMemo<{ id: DetailTab; label: string; enabled: boolean }[]>(
    () => [
      { id: "messages", label: "Messages", enabled: true },
      { id: "event-types", label: "Event Types", enabled: true },
      { id: "alerts", label: "Alerts", enabled: isSubscriptionTabEnabled },
      { id: "audit", label: "Audit", enabled: true },
    ],
    [isSubscriptionTabEnabled],
  );

  if (endpointIsInvalid) {
    return (
      <NotFoundPage errMsg={"No endpoint found with name: " + endpointId} />
    );
  }

  return (
    <Page title={endpointId} subtitle="Endpoint details">
      <div className="flex flex-col gap-4 w-full">
        <TabStrip tabs={tabs} activeTab={activeTab} onChange={selectTab} />
        {/* Mount on first visit; retain visited panels to preserve filters and pages. */}
        <div className={activeTab === "messages" ? "" : "hidden"}>
          <EventsPanel endpointId={endpointId} />
        </div>
        {visitedTabs.has("event-types") && (
          <div className={activeTab === "event-types" ? "" : "hidden"}>
            <EventTypesPanel endpointId={endpointId} />
          </div>
        )}
        {visitedTabs.has("alerts") && (
          <div className={activeTab === "alerts" ? "" : "hidden"}>
            <SubscriptionsTab
              initialSubscriptions={alertSubscriptions}
              setIsTabEnabled={setIsSubscriptionTabEnabled}
            />
          </div>
        )}
        {visitedTabs.has("audit") && (
          <div className={activeTab === "audit" ? "" : "hidden"}>
            <AuditTab endpointId={endpointId} />
          </div>
        )}
      </div>
    </Page>
  );
};

interface TabStripProps {
  tabs: { id: DetailTab; label: string; enabled: boolean }[];
  activeTab: DetailTab;
  onChange: (tab: DetailTab) => void;
}

const TabStrip: React.FC<TabStripProps> = ({ tabs, activeTab, onChange }) => (
  <div role="tablist" className="flex gap-1 border-b border-border -mb-1">
    {tabs.map((t) => {
      const isActive = activeTab === t.id;
      const isDisabled = !t.enabled;
      return (
        <button
          key={t.id}
          role="tab"
          type="button"
          aria-selected={isActive}
          aria-disabled={isDisabled || undefined}
          disabled={isDisabled}
          onClick={() => onChange(t.id)}
          className={cn(
            "bg-transparent border-0 px-[18px] py-[11px] text-[13.5px] font-semibold cursor-pointer",
            "border-b-2 border-transparent -mb-px inline-flex items-center gap-2",
            "transition-colors focus:outline-none focus:ring-2 focus:ring-primary focus:ring-inset",
            isActive
              ? "text-primary-600 border-b-primary bg-gradient-to-b from-transparent to-primary-tint"
              : "text-ink-2 hover:text-ink",
            isDisabled && "opacity-40 cursor-not-allowed hover:text-ink-2",
          )}
        >
          {t.label}
        </button>
      );
    })}
  </div>
);

export default EndpointDetails;
