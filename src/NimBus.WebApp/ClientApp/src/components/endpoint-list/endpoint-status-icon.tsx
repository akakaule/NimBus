import type { SVGProps } from "react";
import { cn } from "lib/utils";
import { EndpointStatus, mapStatusToColor } from "functions/endpoint.functions";
import {
  CheckCircleIcon,
  ClockIcon,
  DatabaseZapIcon,
  MinusCircleIcon,
  UnplugIcon,
  XCircleIcon,
  ZapOffIcon,
} from "./icons";

type Glyph = (props: SVGProps<SVGSVGElement>) => React.JSX.Element;

const ICONS: Record<EndpointStatus, Glyph> = {
  [EndpointStatus.Healthy]: CheckCircleIcon,
  [EndpointStatus.Impacted]: MinusCircleIcon,
  [EndpointStatus.Failed]: XCircleIcon,
  [EndpointStatus.Pending]: ClockIcon,
  [EndpointStatus.Disabled]: ZapOffIcon,
  [EndpointStatus.MissingSubscription]: UnplugIcon,
  [EndpointStatus.StorageUnavailable]: DatabaseZapIcon,
};

const COLOR_CLASSES: Record<string, string> = {
  green: "text-green-500",
  red: "text-red-500",
  yellow: "text-yellow-500",
  teal: "text-teal-500",
  purple: "text-purple-500",
  gray: "text-gray-500",
};

export const endpointStatusColorClass = (status: EndpointStatus): string =>
  COLOR_CLASSES[mapStatusToColor(status)] ?? "text-gray-500";

interface IEndpointStatusIcon {
  status: EndpointStatus;
  className?: string;
}

// Single source for the status glyphs so the Status column and anything else
// showing a status can never drift apart.
const EndpointStatusIcon = ({ status, className }: IEndpointStatusIcon) => {
  const Icon = ICONS[status] ?? CheckCircleIcon;
  return (
    <Icon
      className={cn("w-5 h-5", endpointStatusColorClass(status), className)}
      aria-label={status}
    />
  );
};

export default EndpointStatusIcon;
