import * as api from "api-client";

// Module-level cache keyed by endpoint ID (static data - never changes during runtime)
const cachedEventTypes = new Map<string, api.Anonymous>();
const pendingRequests = new Map<string, Promise<api.Anonymous>>();

export const getEventTypesByEndpoint = async (
  endpointId: string,
): Promise<api.Anonymous> => {
  // Return cached value if available
  const cached = cachedEventTypes.get(endpointId);
  if (cached) {
    return cached;
  }

  // Deduplicate concurrent requests for same endpoint
  const pending = pendingRequests.get(endpointId);
  if (pending) {
    return pending;
  }

  // Make single request and cache result
  const client = new api.Client(api.CookieAuth());
  const request = client
    .getEventtypesByEndpointId(endpointId)
    .then((result) => {
      cachedEventTypes.set(endpointId, result);
      pendingRequests.delete(endpointId);
      return result;
    });

  pendingRequests.set(endpointId, request);
  return request;
};
