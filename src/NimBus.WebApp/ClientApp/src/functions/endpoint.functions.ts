import {
  EndpointStatusCount,
  EndpointSubscription,
  IEndpointStatusCount,
} from "api-client";

export enum EndpointStatus {
  Healthy = "Healthy",
  Impacted = "Impacted",
  Failed = "Failed",
  Pending = "Pending",
  Disabled = "Disabled",
}

export const getEndpointStatus = (
  props: IEndpointStatusCount,
): EndpointStatus => {
  if (props.subscriptionStatus! === "disabled") {
    return EndpointStatus.Disabled;
  } else if (props.failedCount! > 0) {
    return EndpointStatus.Failed;
  } else if (props.deferredCount! > 0) {
    return EndpointStatus.Impacted;
  } else if (props.pendingCount! > 0) {
    return EndpointStatus.Pending;
  } else return EndpointStatus.Healthy;
};

export const mapStatusToColor = (status: EndpointStatus): string => {
  switch (status) {
    case EndpointStatus.Healthy:
      return "green";
    case EndpointStatus.Failed:
      return "red";
    case EndpointStatus.Impacted:
      return "yellow";
    case EndpointStatus.Pending:
      return "teal";
    case EndpointStatus.Disabled:
      return "purple";
  }
};

export const formatMoment = (moment?: moment.Moment, slim = false): string => {
  const formatString = slim ? "DD/MM/YY HH:mm" : "DD/MM/YYYY HH:mm";
  if (moment === undefined) return "";
  return typeof moment?.format === "function"
    ? moment.format(formatString)
    : "Invalid timestamp";
};
