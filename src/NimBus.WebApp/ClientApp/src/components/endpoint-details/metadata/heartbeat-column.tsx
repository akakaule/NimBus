import * as api from "api-client";
import { formatMoment } from "functions/endpoint.functions";
import React from "react";
import { Tooltip } from "components/ui/tooltip";
import "./metadata.css";

// Icons
const QuestionIcon = ({ className }: { className?: string }) => (
  <svg className={className} fill="currentColor" viewBox="0 0 24 24">
    <path
      fillRule="evenodd"
      d="M2.25 12c0-5.385 4.365-9.75 9.75-9.75s9.75 4.365 9.75 9.75-4.365 9.75-9.75 9.75S2.25 17.385 2.25 12zm11.378-3.917c-.89-.777-2.366-.777-3.255 0a.75.75 0 01-.988-1.129c1.454-1.272 3.776-1.272 5.23 0 1.513 1.324 1.513 3.518 0 4.842a3.75 3.75 0 01-.837.552c-.676.328-1.028.774-1.028 1.152v.75a.75.75 0 01-1.5 0v-.75c0-1.279 1.06-2.107 1.875-2.502.182-.088.351-.199.503-.331.83-.727.83-1.857 0-2.584zM12 18a.75.75 0 100-1.5.75.75 0 000 1.5z"
      clipRule="evenodd"
    />
  </svg>
);

const TimeIcon = ({ className }: { className?: string }) => (
  <svg className={className} fill="currentColor" viewBox="0 0 24 24">
    <path
      fillRule="evenodd"
      d="M12 2.25c-5.385 0-9.75 4.365-9.75 9.75s4.365 9.75 9.75 9.75 9.75-4.365 9.75-9.75S17.385 2.25 12 2.25zM12.75 6a.75.75 0 00-1.5 0v6c0 .414.336.75.75.75h4.5a.75.75 0 000-1.5h-3.75V6z"
      clipRule="evenodd"
    />
  </svg>
);

interface IMetadataColumnProps {
  metadata: api.Metadata | undefined;
}

const HeartbeatColumn = (props: IMetadataColumnProps) => {
  const [endpointMetadata, setendpointMetadata] = React.useState<
    api.Metadata | undefined
  >(props.metadata);
  const [heartbeatActive, setHeartbeatActive] = React.useState<boolean>(true);
  const [heartbeatDataAviable, setHeartbeatDataAviable] =
    React.useState<boolean>(false);

  React.useEffect(() => {
    if (
      endpointMetadata?.heartBeats &&
      endpointMetadata.heartBeats.length !== 0
    ) {
      setHeartbeatDataAviable(true);
    } else {
      setHeartbeatDataAviable(false);
    }
  }, [endpointMetadata]);

  const On = (scale: number, margin: string, top: number) => {
    let height = 71.5 * scale;
    return (
      <div
        className="heart-rate relative"
        style={{ margin, height: height + "px", top }}
      >
        <svg
          version="1.0"
          xmlns="http://www.w3.org/2000/svg"
          xmlnsXlink="http://www.w3.org/1999/xlink"
          x="0px"
          y="0px"
          width="75px"
          height="36.5px"
          viewBox="0 0 75 36.5"
          enableBackground="new 0 0 75 36.5"
          xmlSpace="preserve"
          transform={"scale(" + scale + ")"}
        >
          <polyline
            fill="none"
            stroke="#c9201a"
            strokeWidth="3"
            strokeMiterlimit="10"
            points="0,22.743 19.257,22.743 22.2975,16.662 25.338,22.743 28.8855 ,22.743 31.419,27.811 35.9795,4.5 40.0335,31.8645 42.0611,22.743 48.6485,22.743 51.6895,20.2095  55.2365 ,22.743 75,22.743"
          />
        </svg>
        <div
          className="fade-in absolute top-0 right-0"
          style={{ height: height + "px" }}
        ></div>
        <div
          className="fade-out absolute top-0 left-0"
          style={{ height: height + "px" }}
        ></div>
      </div>
    );
  };

  const Off = (scale: number, margin: string, top: number) => {
    let height = 71.5 * scale;

    return (
      <div
        className="heart-rate relative"
        style={{ margin, height: height + "px", top }}
      >
        <svg
          version="1.0"
          xmlns="http://www.w3.org/2000/svg"
          xmlnsXlink="http://www.w3.org/1999/xlink"
          x="0px"
          y="0px"
          width="75px"
          height="36.5px"
          viewBox="0 0 75 36.5"
          enableBackground="new 0 0 75 36.5"
          xmlSpace="preserve"
          transform={"scale(" + scale + ")"}
        >
          <polyline
            fill="none"
            stroke="#c9201a"
            strokeWidth="3"
            strokeMiterlimit="10"
            points="0,22.743 19.257,22.743 22.2975,22.743 25.338,22.743 28.8855 ,22.743 31.419,22.743 35.9795,22.743 40.0335,22.743 42.0611,22.743 48.6485,22.743 51.6895,22.743  55.2365 ,22.743 75,22.743"
          />
        </svg>
        <div
          className="fade-in absolute top-0 right-0"
          style={{ height: height + "px" }}
        ></div>
        <div
          className="fade-out absolute top-0 left-0"
          style={{ height: height + "px" }}
        ></div>
      </div>
    );
  };

  const Unknown = () => (
    <div className="flex justify-center">
      <QuestionIcon className="w-8 h-8" />
    </div>
  );

  const Pending = () => (
    <div className="flex justify-center">
      <TimeIcon className="w-8 h-8" />
    </div>
  );

  function heartbeatStatus(status: string) {
    switch (status) {
      case "On":
        return On(1.2, "0px auto", 5);
      case "Off":
        return Off(1.2, "0px auto", 5);
      case "Unknown":
        return <Unknown />;
      case "Pending":
        return <Pending />;
      default:
        return <Unknown />;
    }
  }

  function heartbeatStatusTable(status: string) {
    switch (status) {
      case "On":
        return On(0.75, "0px 0px 0px -20px", 0);
      case "Off":
        return Off(0.75, "0px 0px 0px -20px", 0);
      case "Unknown":
        return <Unknown />;
      case "Pending":
        return <Pending />;
      default:
        return <Unknown />;
    }
  }

  const HasHeartbeatData = () => (
    <div>
      <p className="font-bold text-center">Heartbeat</p>
      <Tooltip
        content={
          "The current heartbeat status of " +
          endpointMetadata?.id +
          " is " +
          endpointMetadata?.endpointHeartbeatStatus!
        }
      >
        <div className="p-3">
          {heartbeatStatus(endpointMetadata?.endpointHeartbeatStatus!)}
        </div>
      </Tooltip>
      <div className="overflow-x-auto">
        <table className="w-full text-sm">
          <thead>
            <tr className="border-b border-border">
              <th className="text-left py-2 px-2 font-semibold">Start</th>
              <th className="text-left py-2 px-2 font-semibold">Received</th>
              <th className="text-left py-2 px-2 font-semibold">Queue (ms)</th>
              <th className="text-left py-2 px-2 font-semibold">Status</th>
            </tr>
          </thead>
          <tbody>
            {endpointMetadata?.heartBeats?.map((heartbeat, idx) => (
              <tr key={idx + "row"} className="border-b border-border">
                <td className="py-2 px-2" key={idx + "start"}>
                  {formatMoment(heartbeat.startTime, true)}
                </td>
                <td className="py-2 px-2" key={idx + "rec"}>
                  {formatMoment(heartbeat.receivedTime, true)}
                </td>
                <td className="py-2 px-2" key={idx + "que"}>
                  {heartbeat.endTime?.diff(heartbeat.startTime)}
                </td>
                <td className="py-2 px-2" key={idx + "status"}>
                  {heartbeatStatusTable(heartbeat.endpointHeartbeatStatus!)}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  );

  const NoHeartbeatData = () => (
    <div>
      <p className="font-bold text-center">Heartbeat</p>
      <div className="p-3">
        <p className="text-center">There is no heartbeat data available</p>
      </div>
    </div>
  );

  return heartbeatDataAviable ? <HasHeartbeatData /> : <NoHeartbeatData />;
};

export default HeartbeatColumn;
