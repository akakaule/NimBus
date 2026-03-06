import { useState, useEffect } from "react";
import { useParams } from "react-router-dom";
import * as api from "api-client";
import Page from "components/page";
import { Spinner } from "components/ui/spinner";
import { Badge } from "components/ui/badge";
import EventTypeEndpointsList from "components/event-types/event-type-endpoints-list";
import EventTypePropertiesTable from "components/event-types/event-type-properties-table";
import EventTypeExamplePayload from "components/event-types/event-type-example-payload";

// External link icon
const ExternalLinkIcon = () => (
  <svg
    className="w-4 h-4 inline-block ml-0.5"
    fill="none"
    stroke="currentColor"
    viewBox="0 0 24 24"
  >
    <path
      strokeLinecap="round"
      strokeLinejoin="round"
      strokeWidth={2}
      d="M10 6H6a2 2 0 00-2 2v10a2 2 0 002 2h10a2 2 0 002-2v-4M14 4h6m0 0v6m0-6L10 14"
    />
  </svg>
);

const EventTypeDetails: React.FC = () => {
  const params = useParams<{ id: string }>();
  const [details, setDetails] = useState<api.EventTypeDetails | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    const fetchData = async () => {
      const client = new api.Client(api.CookieAuth());
      try {
        const result = await client.getEventtypesEventtypeid(params.id!);
        setDetails(result);
      } catch (err) {
        setError("Failed to load event type details");
        console.error(err);
      } finally {
        setLoading(false);
      }
    };
    fetchData();
  }, [params.id]);

  if (loading) {
    return (
      <Page title="Event Type Details" backbutton backUrl="/EventTypes">
        <div className="flex items-center justify-center w-full h-[200px]">
          <Spinner size="xl" color="primary" />
        </div>
      </Page>
    );
  }

  if (error || !details) {
    return (
      <Page title="Event Type Details" backbutton backUrl="/EventTypes">
        <div className="flex items-center justify-center w-full h-[200px]">
          <p className="text-red-500">{error || "Event type not found"}</p>
        </div>
      </Page>
    );
  }

  const eventType = details.eventType;

  return (
    <Page
      title={eventType?.name || "Event Type Details"}
      backbutton
      backUrl="/EventTypes"
    >
      <div className="w-full">
        <div className="flex flex-col gap-6">
          {/* Header Section */}
          <div>
            <div className="flex gap-2 mb-3">
              <Badge
                variant="primary"
                className="bg-purple-100 text-purple-800"
              >
                {eventType?.namespace}
              </Badge>
            </div>

            <p className="text-muted-foreground mb-3">
              {eventType?.description || "No description available"}
            </p>

            {details.codeRepoLink && (
              <a
                href={details.codeRepoLink}
                target="_blank"
                rel="noopener noreferrer"
                className="text-blue-500 text-sm hover:underline"
              >
                View Source Code <ExternalLinkIcon />
              </a>
            )}
          </div>

          <hr className="border-border" />

          {/* Producers and Consumers */}
          <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
            <EventTypeEndpointsList
              title="Producers"
              endpoints={details.producers || []}
              colorScheme="green"
            />
            <EventTypeEndpointsList
              title="Consumers"
              endpoints={details.consumers || []}
              colorScheme="blue"
            />
          </div>

          {/* Properties Table */}
          <EventTypePropertiesTable properties={eventType?.properties || []} />

          {/* Example Payload */}
          {eventType && <EventTypeExamplePayload eventType={eventType} />}
        </div>
      </div>
    </Page>
  );
};

export default EventTypeDetails;
