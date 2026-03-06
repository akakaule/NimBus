import React, { ReactNode } from "react";
import * as api from "api-client";
import { Link } from "react-router-dom";
import { cn } from "lib/utils";
import {
  EndpointStatus,
  getEndpointStatus,
  mapStatusToColor,
} from "functions/endpoint.functions";

type EndpointStatusCardProps = {
  statusCount: api.IEndpointStatusCount;
  heartbeatStatus: string;
};

const EndpointStatusCard = (props: EndpointStatusCardProps) => {
  const status: EndpointStatus = getEndpointStatus(props.statusCount);

  const getStatusBgClass = (status: EndpointStatus): string => {
    const color = mapStatusToColor(status);
    switch (color) {
      case "green":
        return "bg-green-500";
      case "red":
        return "bg-red-500";
      case "yellow":
        return "bg-yellow-500";
      case "teal":
        return "bg-teal-500";
      case "purple":
        return "bg-purple-500";
      default:
        return "bg-gray-500";
    }
  };

  const getStatusBadgeClass = (status: EndpointStatus): string => {
    const color = mapStatusToColor(status);
    switch (color) {
      case "green":
        return "bg-green-100 text-green-800";
      case "red":
        return "bg-red-100 text-red-800";
      case "yellow":
        return "bg-yellow-100 text-yellow-800";
      case "teal":
        return "bg-teal-100 text-teal-800";
      case "purple":
        return "bg-purple-100 text-purple-800";
      default:
        return "bg-gray-100 text-gray-800";
    }
  };

  const getHeartbeatBadgeClass = (heartbeatStatus: string): string => {
    switch (heartbeatStatus?.toLowerCase()) {
      case "on":
        return "bg-green-100 text-green-800";
      case "off":
        return "bg-red-100 text-red-800";
      case "pending":
        return "bg-teal-100 text-teal-800";
      case "unknown":
      default:
        return "bg-purple-100 text-purple-800";
    }
  };

  const header = (
    <div className={cn("p-4 overflow-hidden", getStatusBgClass(status))}>
      <h3 className="text-lg font-semibold text-white truncate">
        {props.statusCount.endpointId !== undefined
          ? props.statusCount.endpointId.charAt(0)?.toUpperCase() +
            props.statusCount.endpointId.slice(1)
          : ""}
      </h3>
    </div>
  );

  const body = (
    <div className="p-4 bg-muted">
      <div className="flex items-baseline">
        <div className="text-foreground font-semibold tracking-wide text-xs uppercase ml-2">
          {getCombinedEventStates(props.statusCount)}
        </div>
      </div>

      <div className="mt-2 flex gap-1.5">
        <span
          className={cn(
            "inline-flex items-center rounded-full px-2 py-0.5 text-xs font-medium",
            getStatusBadgeClass(status),
          )}
        >
          {status.toUpperCase()}
        </span>

        <span
          className={cn(
            "inline-flex items-center rounded-full px-2 py-0.5 text-xs font-medium",
            getHeartbeatBadgeClass(props.heartbeatStatus),
          )}
        >
          {props.heartbeatStatus.toUpperCase()}
        </span>
      </div>

      <div className="flex justify-end text-foreground text-sm font-light pt-2">
        {props.statusCount.eventTime && (
          <span>Last updated {props.statusCount.eventTime?.fromNow()}</span>
        )}
      </div>
    </div>
  );

  return (
    <Link
      to={`/Endpoints/Details/${props.statusCount.endpointId}`}
      className="rounded-lg overflow-hidden shadow-md hover:shadow-xl transition-shadow duration-300"
    >
      {header}
      {body}
    </Link>
  );
};

const getCombinedEventStates = (props: api.IEndpointStatusCount): ReactNode => {
  const pending = props.pendingCount!;
  const deferred = props.deferredCount!;
  const failed = props.failedCount!;
  return (
    <React.Fragment>
      {pending} pending &bull; {deferred} deferred &bull; {failed} failed
    </React.Fragment>
  );
};

export default EndpointStatusCard;
