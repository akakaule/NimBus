const trimTrailingSlashes = (url: string) => url.replace(/\/+$/, '');

/** The Business Central look-alike (quotes are made there). */
export const BC_WEB_URL = trimTrailingSlashes(import.meta.env.VITE_BC_WEB_URL || 'http://localhost:5293');

/** nimbus-ops, the NimBus operator WebApp that shows every message between the two systems. */
export const NIMBUS_OPS_URL = trimTrailingSlashes(import.meta.env.VITE_NIMBUS_OPS_URL || 'https://localhost:18543');

export type NimBusEndpoint = 'BusinessCentralEndpoint' | 'D365SalesEndpoint';

/** A Business Central quote card. */
export const bcQuoteUrl = (bcQuoteId: string) => `${BC_WEB_URL}/quotes/${encodeURIComponent(bcQuoteId)}`;

/** Business Central's list of the opportunities CRM sent; a Business Central user makes the quote from there. */
export const BC_CRM_OPPORTUNITIES_URL = `${BC_WEB_URL}/crm-opportunities`;

/** An endpoint's messages in nimbus-ops, filtered to one session (the account id is the session key). */
export const nimbusEndpointUrl = (endpoint: NimBusEndpoint, sessionId: string) =>
  `${NIMBUS_OPS_URL}/Endpoints/Details/${endpoint}?sessionId=${encodeURIComponent(sessionId)}`;
