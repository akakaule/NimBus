import * as React from "react";
import * as api from "api-client";
import { Badge } from "components/ui/badge";
import { Button } from "components/ui/button";
import { formatMoment } from "functions/endpoint.functions";

interface ILogListingProps {
  logs: api.EventLogEntry[];
}

export default function LogListing(props: ILogListingProps) {
  return (
    <div className="w-full">
      {props.logs?.map((l, index) => {
        const [show, setShow] = React.useState(false);
        const handleToggle = () => setShow(!show);
        return (
          <div key={index} className="border rounded-lg p-4 mb-4">
            <Badge>{l.messageType}</Badge> {formatMoment(l?.timeStamp)} by{" "}
            <b>{l.from}</b>
            <p>{l.text}</p>
            <br />
            <div
              className={`overflow-hidden transition-all duration-200 ${
                show ? "max-h-[1000px] opacity-100" : "max-h-0 opacity-0"
              }`}
            >
              <table className="text-sm w-auto">
                <tbody>
                  <tr className="hover:bg-accent">
                    <td className="py-2 pr-4">
                      <b>EventType</b>
                    </td>
                    <td className="py-2">{l.eventType}</td>
                  </tr>
                  <tr className="hover:bg-accent">
                    <td className="py-2 pr-4">
                      <b>EventId</b>
                    </td>
                    <td className="py-2">{l.eventId}</td>
                  </tr>
                  <tr className="hover:bg-accent">
                    <td className="py-2 pr-4">
                      <b>SessionId</b>
                    </td>
                    <td className="py-2">{l.sessionId}</td>
                  </tr>
                  <tr className="hover:bg-accent">
                    <td className="py-2 pr-4">
                      <b>MessageId</b>
                    </td>
                    <td className="py-2">{l.messageId}</td>
                  </tr>
                  <tr className="hover:bg-accent">
                    <td className="py-2 pr-4">
                      <b>CorelationId</b>
                    </td>
                    <td className="py-2">{l.correlationId}</td>
                  </tr>
                  <tr className="hover:bg-accent">
                    <td className="py-2 pr-4">
                      <b>Published By</b>
                    </td>
                    <td className="py-2">{l.from}</td>
                  </tr>
                  <tr className="hover:bg-accent">
                    <td className="py-2 pr-4">
                      <b>Payload</b>
                    </td>
                    <td className="py-2">
                      <code className="bg-muted px-2 py-1 rounded text-sm">
                        {l.payload}
                      </code>
                    </td>
                  </tr>
                  <tr className="hover:bg-accent">
                    <td className="py-2 pr-4">
                      <b>MessageType</b>
                    </td>
                    <td className="py-2">{l.messageType}</td>
                  </tr>
                  <tr className="hover:bg-accent">
                    <td className="py-2 pr-4">
                      <b>Is Deferred</b>
                    </td>
                    <td className="py-2">{l.isDeferred}</td>
                  </tr>
                </tbody>
              </table>
            </div>
            <br />
            <Button size="xs" onClick={handleToggle}>
              {show ? "Hide Details" : "Show Details"}
            </Button>
          </div>
        );
      })}
    </div>
  );
}
