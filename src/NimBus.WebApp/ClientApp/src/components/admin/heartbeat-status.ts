import type { BadgeVariant } from "components/ui/badge";

// Shared by the endpoint heartbeat table and the platform-services table so a
// given status reads the same in both. The server sends these five tokens as
// plain strings (HeartbeatOverviewRow.status / ServiceHealthRow.status);
// anything unrecognized degrades to Unknown rather than rendering a raw token.
export type HeartbeatStatus =
  | "On"
  | "Pending"
  | "Off"
  | "Unsupported"
  | "Unknown";

export const statusVariants: Record<HeartbeatStatus, BadgeVariant> = {
  On: "success",
  Pending: "warning",
  Off: "error",
  Unsupported: "unsupported",
  Unknown: "secondary",
};

/**
 * Tooltip text per status. Unsupported is the one that needs explaining: the
 * endpoint answered, so it is NOT down — it just runs an SDK from before the
 * heartbeat handler existed.
 */
export const statusHints: Record<HeartbeatStatus, string> = {
  On: "Answered the last heartbeat.",
  Pending: "Heartbeat sent, waiting for the answer.",
  Off: "No answer before the timeout.",
  Unsupported:
    "Reachable, but running a pre-heartbeat SDK that cannot answer the probe.",
  Unknown: "No probe has settled yet.",
};

export function normalizeStatus(status?: string): HeartbeatStatus {
  if (
    status === "On" ||
    status === "Pending" ||
    status === "Off" ||
    status === "Unsupported"
  ) {
    return status;
  }

  return "Unknown";
}
