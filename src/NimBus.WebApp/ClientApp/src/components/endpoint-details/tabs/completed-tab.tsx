import * as React from "react";
import * as api from "api-client";
import DataTable, {
  ITableHeadAction,
  ITableRow,
  ITableHeadCell,
} from "components/data-table";
import { useParams } from "react-router-dom";
import { formatMoment } from "functions/endpoint.functions";
import TruncatedGuid from "components/common/truncated-guid";

interface ICompletedTabProps {
  setIsTabEnabled: React.Dispatch<React.SetStateAction<boolean>>;
}

const CompletedTab = (props: ICompletedTabProps) => {
  const client = new api.Client(api.CookieAuth());
  const params = useParams();
  const [completedEvents, setCompletedEvents] = React.useState<api.Event[]>([]);
  const [rows, setRows] = React.useState<ITableRow[]>([]);
  const [continuationToken, setContinuationToken] = React.useState<string>();

  React.useEffect(() => {
    props.setIsTabEnabled(false);
    const fetchData = async () => {
      const filter: api.EventFilter = new api.EventFilter();
      filter.endpointId = params.id!;
      filter.resolutionStatus = [api.ResolutionStatus.Completed];

      const reqBody: api.SearchRequest = new api.SearchRequest();
      reqBody.eventFilter = filter;

      const tempCompletedEventsResponse =
        await client.postApiEventEndpointIdGetByFilter(params.id!, reqBody);
      setContinuationToken(tempCompletedEventsResponse.continuationToken);
      const tempCompletedEvents = tempCompletedEventsResponse.events!;
      setCompletedEvents(tempCompletedEvents);

      if (tempCompletedEvents.length > 0) props.setIsTabEnabled(true);
      else props.setIsTabEnabled(false);
    };

    fetchData();
  }, []);

  React.useEffect(() => {
    setRows(mapCompletedEvents());
  }, [completedEvents]);

  const nextPage = async () => {
    if (
      continuationToken !== "" &&
      continuationToken !== null &&
      continuationToken !== "null"
    ) {
      const filter: api.EventFilter = new api.EventFilter();
      filter.endpointId = params.id!;
      filter.resolutionStatus = [api.ResolutionStatus.Completed];

      const reqBody: api.SearchRequest = new api.SearchRequest();
      reqBody.eventFilter = filter;
      reqBody.continuationToken = continuationToken;

      const tempCompletedEventsResponse =
        await client.postApiEventEndpointIdGetByFilter(params.id!, reqBody);

      setContinuationToken(tempCompletedEventsResponse.continuationToken);
      const tempCompletedEvents = tempCompletedEventsResponse.events!;

      //Append unique data
      setCompletedEvents(completedEvents.concat(tempCompletedEvents));
    }
  };

  const mapCompletedEvents = () => {
    const iRows = completedEvents.map((item) => {
      const row: ITableRow = {
        route: `/Message/Index/${params.id!}/${item.eventId}`,
        id: item.eventId!,
        bodyActions: [],
        data: new Map([
          [
            "eventId",
            {
              value: <TruncatedGuid guid={item.eventId} />,
              searchValue: item.eventId!,
            },
          ],
          [
            "sessionId",
            {
              value: <TruncatedGuid guid={item.sessionId} />,
              searchValue: item.sessionId!,
            },
          ],
          [
            "eventTypeId",
            {
              value: item?.eventTypeId,
              searchValue: item?.eventTypeId || "",
            },
          ],
          [
            "updated",
            {
              value: formatMoment(item.updatedAt!, true),
              searchValue: formatMoment(item.updatedAt!, true),
            },
          ],
          [
            "added",
            {
              value: formatMoment(item?.enqueuedTimeUtc, true),
              searchValue: formatMoment(item?.enqueuedTimeUtc) || "",
            },
          ],
        ]),
      };
      return row;
    });
    return iRows;
  };

  const headCells: ITableHeadCell[] = [
    { id: "eventId", label: "Event Id", numeric: false, width: "15%" },
    { id: "sessionId", label: "Session Id", numeric: false, width: "15%" },
    { id: "eventTypeId", label: "Event Type", numeric: false, width: "30%" },
    { id: "updated", label: "Updated(UTC)", numeric: false, width: "20%" },
    { id: "added", label: "Added(UTC)", numeric: false, width: "20%" },
  ];

  const headActions: ITableHeadAction[] = [];

  return (
    <DataTable
      headCells={headCells}
      headActions={headActions}
      rows={rows}
      withCheckboxes={false}
      noDataMessage="No events available"
      isLoading={false}
      count={rows.length}
      hideDense={true}
      dataRowsPerPage={20}
      onPageChange={nextPage}
    />
  );
};

export default CompletedTab;
