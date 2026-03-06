import * as React from "react";
import { formatMoment } from "functions/endpoint.functions";
import { PendingEvent } from "pages/event-details";

interface IPendingListing {
  events: PendingEvent[];
  onPageChange?: () => void;
}

export default function PendingListing(props: IPendingListing) {
  const count = props.events.length ?? 0;
  const [page, setPage] = React.useState(0);
  const [rowsPerPage, setRowsPerPage] = React.useState(6);
  const onPageChange = props.onPageChange;

  const handleChangePage = (newPage: number) => {
    setPage(newPage);
    if (onPageChange !== undefined) {
      onPageChange();
    }
  };

  const handleChangeRowsPerPage = (
    event: React.ChangeEvent<HTMLSelectElement>,
  ) => {
    setRowsPerPage(parseInt(event.target.value, 10));
    setPage(0);
  };

  const totalPages = Math.ceil(count / rowsPerPage);

  return (
    <div className="w-full mr-4 flex flex-col">
      <div className="overflow-auto flex-1">
        {props.events
          .sort((a, b) =>
            a.message.enqueuedTimeUtc! > b.message.enqueuedTimeUtc! ? 1 : -1,
          )
          .slice(page * rowsPerPage, page * rowsPerPage + rowsPerPage)
          .map((e, index) => {
            return (
              <div key={index} className="border rounded-lg mb-4 p-4">
                <h4 className="text-lg font-semibold">
                  {e.message?.eventTypeId} - {e.status}
                </h4>
                <p>
                  {formatMoment(e.message.enqueuedTimeUtc)}
                  <a
                    href={`/Endpoints/Details/${e.message?.originatingFrom}`}
                    className="text-primary hover:underline ml-2"
                  >
                    {e.message?.originatingFrom}
                  </a>
                </p>
                <br />
                <p>
                  <b>EventId</b>
                </p>
                <p>{e.message.eventId}</p>
                <br />
                <p>
                  <b>Payload</b>
                </p>
                <pre className="bg-muted p-2 rounded text-sm overflow-x-auto">
                  {JSON.stringify(JSON.parse(e.message.eventContent!), null, 2)}
                </pre>
                <br />
                <p>
                  <b>To</b>
                </p>
                <p>
                  <a
                    href={`/Endpoints/Details/${e.message?.to}`}
                    className="text-primary hover:underline"
                  >
                    {e.message?.to}
                  </a>
                </p>
              </div>
            );
          })}
      </div>
      {/* Pagination */}
      {totalPages > 1 && (
        <div className="flex items-center justify-between px-4 py-3 border-t border-border mt-4">
          <div className="text-sm text-muted-foreground">
            Page {page + 1} of {totalPages}
          </div>
          <div className="flex items-center gap-2">
            <button
              className="px-3 py-1 text-sm border rounded-md disabled:opacity-50"
              onClick={() => handleChangePage(page - 1)}
              disabled={page === 0}
            >
              Previous
            </button>
            <button
              className="px-3 py-1 text-sm border rounded-md disabled:opacity-50"
              onClick={() => handleChangePage(page + 1)}
              disabled={page >= totalPages - 1}
            >
              Next
            </button>
          </div>
        </div>
      )}
    </div>
  );
}
