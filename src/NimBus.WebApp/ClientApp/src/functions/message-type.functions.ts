// Message types travel as the values api-spec.yaml declares — camelCase, e.g.
// "eventRequest". The server sent PascalCase for as long as it serialized CLR
// names rather than the contract, and stored history still carries that spelling,
// so every lookup here normalises the first character and accepts both.
const normalize = (messageType: string): string =>
  messageType.length > 0 ? messageType[0].toLowerCase() + messageType.slice(1) : "";

/** Operator-facing labels, keyed by the contract value. */
const LABELS: Record<string, string> = {
  eventRequest: "Event Request",
  resolutionResponse: "Completed",
  errorResponse: "Error",
  retryRequest: "Retry",
  deferralResponse: "Deferred",
  resubmissionRequest: "Resubmission",
  skipResponse: "Skipped",
  skipRequest: "Skip Request",
  continuationRequest: "Continuation",
  unsupportedRequest: "Unsupported",
  pendingHandoffResponse: "Awaiting External",
  handoffCompletedRequest: "Handoff Completed",
  handoffFailedRequest: "Handoff Failed",
  unknown: "Unknown",
};

export const messageTypeKey = normalize;

/**
 * A message type as an operator should read it. Falls back to the raw value for
 * anything the catalog doesn't name, so a new type shows up rather than vanishing.
 */
export const formatMessageType = (messageType: string | undefined): string => {
  if (!messageType) return "";
  return LABELS[normalize(messageType)] ?? messageType;
};
