import * as React from "react";
import * as api from "api-client";
import Page from "components/page";
import HistoryListing from "components/event-details/history-listing";
import LogListing from "components/event-details/log-listing";
import MessageListing from "components/event-details/message-listing";
import { useParams, useNavigate } from "react-router-dom";
import TabSelection from "components/tab-selection";
import Loading from "components/loading/loading";
import AuditListing from "components/event-details/audit-listing";
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

const EventDetails = (props: EventDetailsProps) => {
  const client = new api.Client(api.CookieAuth());
  const params = useParams();
  const navigate = useNavigate();

  const [cosmosEvent, setCosmosEvent] = useState<api.Event>();
  const [eventTypes, setEventTypes] = useState<api.EventType[]>([]);
  const [logs, setLogs] = useState<api.EventLogEntry[]>([]);
  const [histories, setHistories] = useState<api.Message[]>([]);
  const [audits, setAudits] = useState<api.MessageAudit[]>([]);
  const [blockedEvents, setBlockedEvents] = useState<BlockedEvent[]>([]);
  const [blockedEventIds, setBlockedEventIds] = useState<api.BlockedEvent[]>(
    [],
  );

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
            tempCosmosEvent.sessionId!,
            tempCosmosEvent.endpointId!,
          )
          .then(async (res) => {
            const tempBlockedEvents = [];

            for (const event of res.slice(0, 6)) {
              const eventIds = await client.getEventIds(
                event.eventId!,
                event.originatingId!,
              );
              tempBlockedEvents.push({
                message: eventIds,
                status: event.status!,
              });
            }
            setBlockedEvents(tempBlockedEvents);
            setBlockedEventIds(res);
          });

      client
        .getEventDetailsLogsId(params.id!, params.endpointId!)
        .then((res) => {
          setLogs(res);
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

  const fetchBlockedEvents = async (startIndex: number, endIndex: number) => {
    const tempBlockedEvents = [];

    for (const event of blockedEventIds.slice(startIndex, endIndex)) {
      const res = await client.getEventIds(
        event.eventId!,
        event.originatingId!,
      );
      if (res) {
        tempBlockedEvents.push({ message: res, status: event.status! });
        setBlockedEvents(tempBlockedEvents);
      }
    }
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
    let blockedCount;
    if (blockedEventIds.length != null) {
      blockedCount =
        blockedEventIds?.length > 99 ? "99+" : blockedEventIds?.length;
    }

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
        name: `Flow`,
        isEnabled: histories.length > 0,
        content: <FlowTimeline messages={histories} audits={audits} key="Flow" />,
      },
      {
        name: `History (${histories.length ?? 0})`,
        isEnabled: histories.length > 0,
        content: <HistoryListing histories={histories} key="History" />,
      },
      {
        name: `Logs (${logs?.length ?? 0})`,
        isEnabled: logs.length > 0,
        content: <LogListing logs={logs!} key="Logs" />,
      },
      {
        name: `Audit (${audits?.length ?? 0})`,
        isEnabled: audits.length > 0,
        content: <AuditListing audits={audits!} key="Audits" />,
      },
      {
        name: `Blocked (${blockedCount})`,
        isEnabled: blockedEventIds.length > 0,
        content: (
          <BlockedListing
            totalItems={blockedEventIds.length}
            fetchBlockedEvents={fetchBlockedEvents}
            events={blockedEvents}
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
