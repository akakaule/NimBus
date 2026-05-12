import * as React from "react";
import * as api from "api-client";
import Page from "components/page";
import MessageListing from "components/event-details/message-listing";
import { useParams, useNavigate } from "react-router-dom";
import TabSelection from "components/tab-selection";
import Loading from "components/loading/loading";
import BlockedListing from "components/event-details/blocked-listing";
import FlowTimeline from "components/event-details/flow-timeline";

const { useEffect, useState } = React;

export interface BlockedEvent {
  message: api.Message;
  status: string;
}

export interface PendingEvent {
  message: api.Message;
  status: string;
}

type EventDetailsProps = {
  backIndex?: number;
};

// Initial blocked-events page size. Server clamps `take` to [1, 200] so this fits comfortably.
const BLOCKED_PAGE_SIZE = 20;

const EventDetails = (props: EventDetailsProps) => {
  const client = new api.Client(api.CookieAuth());
  const params = useParams();
  const navigate = useNavigate();

  const [cosmosEvent, setCosmosEvent] = useState<api.Event>();
  const [eventTypes, setEventTypes] = useState<api.EventType[]>([]);
  const [histories, setHistories] = useState<api.Message[]>([]);
  const [audits, setAudits] = useState<api.MessageAudit[]>([]);
  const [blockedEvents, setBlockedEvents] = useState<BlockedEvent[]>([]);
  const [blockedTotal, setBlockedTotal] = useState<number>(0);

  // Resolve the BlockedEvent ID list into displayable rows (one detail fetch per ID).
  // The server now returns only the requested page, so this loop's bound is the page size,
  // not the full set of blocked siblings on the session.
  const enrichBlockedItems = async (items: api.BlockedEvent[]): Promise<BlockedEvent[]> => {
    const enriched: BlockedEvent[] = [];
    for (const event of items) {
      const message = await client.getEventIds(event.eventId!, event.originatingId!);
      if (message) {
        enriched.push({ message, status: event.status! });
      }
    }
    return enriched;
  };

  useEffect(() => {
    const fetchData = async () => {
      const tempCosmosEvent = await client.getEventId(
        params.id!,
        params.endpointId!,
      );

      setCosmosEvent(tempCosmosEvent);

      if (
        tempCosmosEvent.resolutionStatus?.toLowerCase() === "failed" ||
        tempCosmosEvent.resolutionStatus?.toLowerCase() === "unsupported" ||
        tempCosmosEvent.resolutionStatus?.toLowerCase() === "deadlettered"
      )
        client
          .getEventBlockedId(
            tempCosmosEvent.endpointId!,
            tempCosmosEvent.sessionId!,
            0,
            BLOCKED_PAGE_SIZE,
          )
          .then(async (page) => {
            const enriched = await enrichBlockedItems(page.items ?? []);
            setBlockedEvents(enriched);
            setBlockedTotal(page.total ?? enriched.length);
          });

      client
        .getEventDetailsHistoryId(params.id!, params.endpointId!)
        .then((res) => {
          setHistories(res);
        });

      client.getMessageAuditsEventId(params.id!).then((res) => {
        setAudits(res);
      });

      client.getEventTypes().then((res) => {
        setEventTypes(res);
      });
    };
    fetchData();
  }, []);

  const fetchBlockedEvents = async (skip: number, take: number) => {
    if (!cosmosEvent?.endpointId || !cosmosEvent?.sessionId) return;
    const page = await client.getEventBlockedId(
      cosmosEvent.endpointId,
      cosmosEvent.sessionId,
      skip,
      take,
    );
    const enriched = await enrichBlockedItems(page.items ?? []);
    setBlockedEvents(enriched);
    setBlockedTotal(page.total ?? enriched.length);
  };

  const reloadEvent = async () => {
    const updated = await client.getEventId(params.id!, params.endpointId!);
    setCosmosEvent(updated);
  };

  const skipEvent = async (eventId: string, messageId: string) => {
    await client.postSkipEventIds(eventId, messageId);
    await reloadEvent();
  };

  const resubmitEvent = async (eventId: string, messageId: string) => {
    await client.postResubmitEventIds(eventId, messageId);
    await reloadEvent();
  };

  const reprocessDeferred = async () => {
    if (!cosmosEvent?.endpointId || !cosmosEvent?.sessionId) return;
    await client.postReprocessDeferred(
      cosmosEvent.endpointId,
      cosmosEvent.sessionId,
    );
    await reloadEvent();
  };

  const reloadAudits = () => {
    client.getMessageAuditsEventId(params.id!).then((res) => {
      setAudits(res);
    });
  };

  const deleteEvent = async () => {
    if (!cosmosEvent) return;
    await client.deleteEventInvalidId(
      cosmosEvent.endpointId!,
      cosmosEvent.eventId!,
      cosmosEvent.sessionId!,
    );
    navigate(`/Endpoints/Details/${params.endpointId!}`);
  };

  const resubmitEventWithChanges = async (
    eventId: string,
    messageId: string,
    body: api.ResubmitWithChanges,
  ) => {
    await client.postResubmitWithChangesEventIds(eventId, messageId, body);
    await reloadEvent();
  };

  const tabs = () => {
    const blockedCount = blockedTotal > 99 ? "99+" : blockedTotal;

    return [
      {
        name: `Message`,
        isEnabled: true,
        content: (
          <MessageListing
            resubmitEventWithChanges={resubmitEventWithChanges}
            resubmitEvent={resubmitEvent}
            skipEvent={skipEvent}
            deleteEvent={deleteEvent}
            reprocessDeferred={reprocessDeferred}
            onCommentAdded={reloadAudits}
            eventTypes={eventTypes}
            eventDetails={cosmosEvent}
            key="Message"
          />
        ),
      },
      {
        name: `Flow (${histories.length + audits.length})`,
        isEnabled: histories.length > 0,
        content: <FlowTimeline messages={histories} audits={audits} key="Flow" />,
      },
      {
        name: `Blocked (${blockedCount})`,
        isEnabled: blockedTotal > 0,
        content: (
          <BlockedListing
            totalItems={blockedTotal}
            fetchBlockedEvents={fetchBlockedEvents}
            events={blockedEvents}
            endpointId={cosmosEvent?.endpointId ?? params.endpointId}
            key="Blocked"
          />
        ),
      },
    ];
  };

  return (
    <>
      {cosmosEvent === undefined ? (
        <div className="flex flex-1 justify-center items-center">
          <Loading />
        </div>
      ) : (
        <Page
          title="Event Details"
          backbutton={true}
          backUrl={`/Endpoints/Details/${params.endpointId!}`}
          backIndex={params.backindex!}
        >
          <TabSelection tabs={tabs()} />
        </Page>
      )}
    </>
  );
};

export default EventDetails;
