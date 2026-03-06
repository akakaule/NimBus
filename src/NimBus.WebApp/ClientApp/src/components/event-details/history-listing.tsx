import * as React from "react";
import * as api from "api-client";
import { formatMoment } from "functions/endpoint.functions";

interface IHistoryListingProps {
  histories: api.Message[];
}

export default function HistoryListings(props: IHistoryListingProps) {
  return (
    <div className="w-full mr-4 flex flex-col">
      <div className="overflow-auto flex-1">
        {props.histories.map((h, index) => {
          return (
            <div key={index} className="border rounded-lg mb-4 p-4">
              <h4 className="text-lg font-semibold">{h.messageType}</h4>
              <p>{formatMoment(h.enqueuedTimeUtc)}</p>
              <br />
              <p>
                <b>
                  {h.errorContent?.exceptionStackTrace != undefined
                    ? "Exception"
                    : ""}
                </b>
              </p>
              <p>{h.errorContent?.exceptionStackTrace}</p>
              <br />
              <p>
                <b>{h.eventContent != undefined ? "Payload" : ""}</b>
              </p>
              <pre className="bg-muted p-2 rounded text-sm overflow-x-auto">
                {h.eventContent != undefined
                  ? JSON.stringify(JSON.parse(h.eventContent), null, 2)
                  : ""}
              </pre>
              <br />
              <p>
                <b>From:</b> {h.from}
              </p>
              <br />
              <p>
                <b>To:</b> {h.to}
              </p>
              <br />
            </div>
          );
        })}
      </div>
    </div>
  );
}
