import * as api from "api-client";

/** A SimulationStatus like the server returns for the bundled Storefront/Billing/Warehouse platform. */
export function simulationStatus(overrides: Record<string, unknown> = {}): api.SimulationStatus {
  return api.SimulationStatus.fromJS({
    allowed: true,
    blockedReason: undefined,
    environment: "dev",
    productionNames: ["live", "prd", "prod", "production"],
    allowedEnvironments: ["dev", "development"],
    enabled: true,
    state: "stopped",
    capped: false,
    maxRateCeilingPerMinute: 1200,
    sessionPrefix: "sim-",
    settings: {
      enabled: true,
      autoStopMinutes: 60,
      rateCeilingPerMinute: 600,
      ownedEndpoints: ["BillingEndpoint"],
    },
    config: {
      speed: 1,
      publishers: [
        {
          endpointId: "StorefrontEndpoint",
          eventTypes: [
            { eventTypeId: "OrderPlaced", enabled: true, ratePerMinute: 10 },
            { eventTypeId: "OrderDeliveryDetailsCaptured", enabled: true, ratePerMinute: 10 },
          ],
        },
      ],
      subscribers: [],
    },
    counters: {
      published: 0,
      handledOk: 0,
      handlerErrors: 0,
      poisoned: 0,
      publishErrors: 0,
      abandonedSends: 0,
      throughputPerMinute: 0,
    },
    recent: [],
    endpoints: [
      { endpointId: "BillingEndpoint", produces: [], consumes: ["OrderPlaced", "OrderDeliveryDetailsCaptured"], owned: true, liveInstanceWarning: false, effectiveMode: "healthy" },
      { endpointId: "StorefrontEndpoint", produces: ["OrderPlaced", "OrderDeliveryDetailsCaptured"], consumes: [], owned: false, liveInstanceWarning: false },
      { endpointId: "WarehouseEndpoint", produces: [], consumes: ["OrderPlaced"], owned: false, liveInstanceWarning: false },
    ],
    ...overrides,
  });
}
