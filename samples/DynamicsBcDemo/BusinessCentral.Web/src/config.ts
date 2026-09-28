const withoutTrailingSlash = (url: string) => url.replace(/\/+$/, '');

/** The Sales Hub look-alike (simulated Dynamics 365 Sales). */
export const D365_WEB_URL = withoutTrailingSlash(import.meta.env.VITE_D365_WEB_URL || 'http://localhost:5283');

/** nimbus-ops, the NimBus operator WebApp. */
export const NIMBUS_OPS_URL = withoutTrailingSlash(import.meta.env.VITE_NIMBUS_OPS_URL || 'https://localhost:18543');

export const d365OpportunityUrl = (opportunityId: string) => `${D365_WEB_URL}/opportunities/${opportunityId}`;

export const d365AccountUrl = (accountId: string) => `${D365_WEB_URL}/accounts/${accountId}`;
