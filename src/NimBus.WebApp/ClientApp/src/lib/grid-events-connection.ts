import * as signalR from "@microsoft/signalr";

// Shared GridEventsHub subscription (spec 020, FR-003). First client-side
// consumer of the hub the server has broadcast on since spec 010 — kept as a
// plain TS module (no React) so future pages can reuse the same connection
// management without inheriting a hook's lifecycle. The hub URL and event
// name mirror the server-side constants in Constants.cs
// (AppEndpoints.GridEventHub / EventSignalNames.EndpointUpdate).

const HUB_URL = "/hubs/gridevents";
const ENDPOINT_UPDATE_EVENT = "endpointupdate";

export type HubState = "connecting" | "connected" | "disconnected";

export interface GridEventsSubscription {
  dispose(): void;
}

/**
 * Opens a GridEventsHub connection and forwards every `endpointupdate`
 * payload to `onUpdate`. Payloads arrive as raw camelCase JSON (the hub
 * serializes EndpointStatusCount directly — NOT generated api-client
 * instances); callers own the boundary conversion.
 *
 * State callbacks fire only on transitions:
 *  - start() resolved / onreconnected  → "connected"
 *  - onreconnecting / onclose / start() rejected → "disconnected"
 *
 * Reconnect policy is delegated entirely to withAutomaticReconnect(); when
 * that gives up (onclose) we report "disconnected" and stop — callers fall
 * back to polling rather than this module retry-looping forever.
 */
export function subscribeEndpointUpdates(
  onUpdate: (raw: unknown) => void,
  onStateChange: (state: HubState) => void,
): GridEventsSubscription {
  const connection = new signalR.HubConnectionBuilder()
    .withUrl(HUB_URL)
    .withAutomaticReconnect()
    .configureLogging(signalR.LogLevel.Warning)
    .build();

  // dispose() triggers stop(), which fires onclose — the guard keeps that
  // self-inflicted close from reporting a phantom "disconnected" to a caller
  // that has already moved on.
  let disposed = false;

  connection.on(ENDPOINT_UPDATE_EVENT, onUpdate);
  connection.onreconnecting(() => {
    if (!disposed) onStateChange("disconnected");
  });
  connection.onreconnected(() => {
    if (!disposed) onStateChange("connected");
  });
  connection.onclose(() => {
    if (!disposed) onStateChange("disconnected");
  });

  connection
    .start()
    .then(() => {
      if (!disposed) onStateChange("connected");
    })
    .catch(() => {
      // Initial negotiate failed (auth, proxy, server down). Automatic
      // reconnect only covers established connections, so this is terminal
      // for the subscription — the caller's polling fallback takes over.
      if (!disposed) onStateChange("disconnected");
    });

  return {
    dispose() {
      disposed = true;
      connection.off(ENDPOINT_UPDATE_EVENT, onUpdate);
      // stop() rejects if called while start() is still negotiating; the
      // subscription is gone either way, so swallow it.
      void connection.stop().catch(() => undefined);
    },
  };
}
