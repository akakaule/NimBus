// Typed client for the Sales Hub simulator's /api/app surface (D365Sales.Api AppEndpoints.cs).
// Property names are the camelCase JSON of the Dataverse-shaped entities; decimals arrive as
// numbers and timestamps as ISO strings.

export type Guid = string;

export type TimelineSource = 'User' | 'Integration' | 'System';

/** cs_bcquotestatus: CRM's "Requested", then Business Central's own quote status. */
export type BcQuoteStatus = 'Requested' | 'Draft' | 'Sent' | 'Accepted' | 'Expired';

export interface SystemUser {
  systemUserId: Guid;
  fullName: string;
  internalEmailAddress: string;
}

export interface Product {
  productId: Guid;
  productNumber: string;
  name: string;
  price: number;
  defaultUnit: string;
}

export interface Account {
  accountId: Guid;
  name: string;
  /** The Business Central customer number once the account is a customer. */
  accountNumber: string | null;
  /** Relationship type: 3 Customer, 8 Prospect. */
  customerTypeCode: number;
  address1Line1: string | null;
  address1City: string | null;
  address1PostalCode: string | null;
  address1Country: string | null;
  telephone1: string | null;
  websiteUrl: string | null;
  csVatNumber: string | null;
  creditLimit: number | null;
  creditOnHold: boolean;
  ownerId: Guid;
  primaryContactId: Guid | null;
  csBcCustomerId: Guid | null;
  csBcContactNumber: string | null;
  csBcBlocked: string | null;
  csBcBalanceDue: number | null;
  csBcPaymentTerms: string | null;
  /** 1 Dynamics 365, 2 Business Central. */
  csMasterDataOwner: number;
  csBcLastSyncedOn: string | null;
  description: string | null;
  createdOn: string;
  modifiedOn: string;
}

export interface ContactSummary {
  contactId: Guid;
  fullName: string;
  emailAddress1: string | null;
  telephone1: string | null;
  jobTitle: string | null;
}

export interface Lead {
  leadId: Guid;
  subject: string;
  firstName: string;
  lastName: string;
  companyName: string;
  emailAddress1: string | null;
  telephone1: string | null;
  jobTitle: string | null;
  address1Line1: string | null;
  address1City: string | null;
  address1PostalCode: string | null;
  address1Country: string | null;
  websiteUrl: string | null;
  csVatNumber: string | null;
  estimatedValue: number | null;
  /** 0 Open, 1 Qualified, 2 Disqualified. */
  stateCode: number;
  ownerId: Guid;
  createdOn: string;
  qualifiedAccountId: Guid | null;
  qualifiedContactId: Guid | null;
  qualifiedOpportunityId: Guid | null;
  qualifiedOpportunityNumber: string | null;
}

export interface OpportunityLine {
  opportunityProductId: Guid;
  opportunityId: Guid;
  sequence: number;
  productNumber: string;
  description: string;
  quantity: number;
  pricePerUnit: number;
  extendedAmount: number;
}

export interface Opportunity {
  opportunityId: Guid;
  /** cs_number, e.g. OPP-10025. */
  csNumber: string;
  name: string;
  /** The account. */
  customerId: Guid;
  parentContactId: Guid | null;
  estimatedValue: number | null;
  estimatedCloseDate: string | null;
  closeProbability: number;
  /** Business process flow stage, e.g. 2-Develop. */
  stepName: string;
  /** 0 Open, 1 Won, 2 Lost. */
  stateCode: number;
  actualValue: number | null;
  actualCloseDate: string | null;
  ownerId: Guid;
  csBcQuoteId: Guid | null;
  csBcQuoteNumber: string | null;
  csBcQuoteStatus: BcQuoteStatus | null;
  csBcOrderNumber: string | null;
  csLinesRevision: number;
  csQuoteRequestRevision: number;
  csQuoteRequestedOn: string | null;
  createdOn: string;
  modifiedOn: string;
  lines: OpportunityLine[];
}

/** A read-only mirror of a Business Central sales quote. */
export interface BcQuote {
  bcQuoteId: Guid;
  quoteNumber: string;
  opportunityId: Guid | null;
  accountId: Guid | null;
  status: string;
  totalAmount: number;
  currencyCode: string;
  validUntil: string | null;
  sentOn: string | null;
  acceptedOn: string | null;
  lastSyncedOn: string;
}

export interface TimelineEntry {
  id: Guid;
  regardingId: Guid;
  title: string;
  detail: string | null;
  source: TimelineSource;
  createdOn: string;
}

export interface PipelineStage {
  stage: string;
  count: number;
  value: number;
}

export interface Dashboard {
  pipeline: PipelineStage[];
  openCount: number;
  openValue: number;
  wonCount: number;
  wonValue: number;
  quotesInBc: number;
  integrationActivity: TimelineEntry[];
}

export interface LeadRow {
  lead: Lead;
  owner: string | null;
}

export interface LeadDetail {
  lead: Lead;
  timeline: TimelineEntry[];
}

export interface AccountRow {
  account: Account;
  owner: string | null;
}

export interface AccountDetail {
  account: Account;
  owner: SystemUser | null;
  contacts: ContactSummary[];
  opportunities: Opportunity[];
  timeline: TimelineEntry[];
}

export interface OpportunityRow {
  opportunity: Opportunity;
  account: string | null;
  owner: string | null;
}

export interface OpportunityDetail {
  opportunity: Opportunity;
  account: Account | null;
  owner: SystemUser | null;
  bcQuotes: BcQuote[];
  timeline: TimelineEntry[];
}

export interface QualifyResult {
  opportunityId: Guid;
  accountId: Guid;
}

export interface AccountEdit {
  name: string;
  address1Line1: string | null;
  address1City: string | null;
  address1PostalCode: string | null;
  address1Country: string | null;
  telephone1: string | null;
  websiteUrl: string | null;
  csVatNumber: string | null;
  ownerId: Guid;
  description: string | null;
}

export interface OpportunityEdit {
  name: string;
  estimatedCloseDate: string | null;
  closeProbability: number;
  stepName: string;
}

export interface OpportunityLineEdit {
  productNumber: string;
  quantity: number;
}

export interface SetLinesResult {
  opportunityId: Guid;
  csLinesRevision: number;
  estimatedValue: number | null;
}

export interface QuoteRequestResult {
  messageId: string;
  revision: number;
  /** True when this lines revision was requested before: same MessageId, so BC's inbox skips it. */
  repeat: boolean;
}

export type CreditStatusValue = 'Ok' | 'NotFound' | 'Unavailable';

/** Business Central's live answer to a credit check (request/reply over NimBus). */
export interface BcCreditStatus {
  accountId: Guid;
  status: CreditStatusValue;
  customerNumber: string | null;
  creditLimit: number | null;
  balanceDue: number | null;
  overdueAmount: number | null;
  availableCredit: number | null;
  /** Empty, Ship, Invoice or All. */
  blocked: string;
  reason: string | null;
  checkedAt: string;
}

/** An API call that failed; the message is the API's own {"error": "..."} text when it sent one. */
export class ApiError extends Error {
  readonly status: number;

  constructor(message: string, status: number) {
    super(message);
    this.name = 'ApiError';
    this.status = status;
  }
}

export function isAbortError(error: unknown): boolean {
  return typeof error === 'object' && error !== null && (error as { name?: unknown }).name === 'AbortError';
}

export function errorMessage(error: unknown): string {
  if (error instanceof Error) return error.message;
  return typeof error === 'string' ? error : 'Something went wrong.';
}

function messageFromBody(body: unknown): string | undefined {
  if (typeof body !== 'object' || body === null || !('error' in body)) return undefined;
  const error = (body as { error: unknown }).error;
  if (typeof error === 'string') return error;
  // The Dataverse-shaped API answers {"error":{"code":"...","message":"..."}}.
  if (typeof error === 'object' && error !== null && typeof (error as { message?: unknown }).message === 'string') {
    return (error as { message: string }).message;
  }
  return undefined;
}

function fallbackMessage(status: number, statusText: string): string {
  if (status === 404) return 'The record was not found. It may have been deleted.';
  if (status === 502 || status === 503 || status === 504) {
    return `The Sales Hub API is not responding (${status}). Is the AppHost running?`;
  }
  return `The Sales Hub API returned ${status}${statusText ? ` ${statusText}` : ''}.`;
}

/** Fetches JSON and turns any non-2xx answer into an ApiError carrying the API's error text. */
export async function json<T>(url: string, init?: RequestInit): Promise<T> {
  const headers = new Headers(init?.headers);
  headers.set('Accept', 'application/json');
  if (init?.body !== undefined && !headers.has('Content-Type')) headers.set('Content-Type', 'application/json');

  let response: Response;
  try {
    response = await fetch(url, { ...init, headers });
  } catch (error) {
    if (isAbortError(error)) throw error;
    throw new ApiError('Cannot reach the Sales Hub API. Is the AppHost running?', 0);
  }

  const text = await response.text();
  let body: unknown;
  if (text) {
    try {
      body = JSON.parse(text);
    } catch {
      body = undefined;
    }
  }

  if (!response.ok) {
    throw new ApiError(messageFromBody(body) ?? fallbackMessage(response.status, response.statusText), response.status);
  }
  return body as T;
}

const get = <T>(url: string, signal?: AbortSignal) => json<T>(url, { signal });
const send = <T>(method: 'POST' | 'PUT', url: string, payload: unknown) =>
  json<T>(url, { method, body: JSON.stringify(payload) });

const id = (value: string) => encodeURIComponent(value);

export const api = {
  users: (signal?: AbortSignal) => get<SystemUser[]>('/api/app/users', signal),
  products: (signal?: AbortSignal) => get<Product[]>('/api/app/products', signal),
  dashboard: (signal?: AbortSignal) => get<Dashboard>('/api/app/dashboard', signal),

  leads: (signal?: AbortSignal) => get<LeadRow[]>('/api/app/leads', signal),
  lead: (leadId: Guid, signal?: AbortSignal) => get<LeadDetail>(`/api/app/leads/${id(leadId)}`, signal),
  qualifyLead: (leadId: Guid, userId: Guid) =>
    send<QualifyResult>('POST', `/api/app/leads/${id(leadId)}/qualify`, { userId }),

  accounts: (signal?: AbortSignal) => get<AccountRow[]>('/api/app/accounts', signal),
  account: (accountId: Guid, signal?: AbortSignal) => get<AccountDetail>(`/api/app/accounts/${id(accountId)}`, signal),
  updateAccount: (accountId: Guid, edit: AccountEdit) =>
    send<Account>('PUT', `/api/app/accounts/${id(accountId)}`, edit),
  checkCredit: (accountId: Guid, userId: Guid) =>
    send<BcCreditStatus>('POST', `/api/app/accounts/${id(accountId)}/credit-check`, { userId }),

  opportunities: (signal?: AbortSignal) => get<OpportunityRow[]>('/api/app/opportunities', signal),
  opportunity: (opportunityId: Guid, signal?: AbortSignal) =>
    get<OpportunityDetail>(`/api/app/opportunities/${id(opportunityId)}`, signal),
  updateOpportunity: (opportunityId: Guid, edit: OpportunityEdit) =>
    send<Opportunity>('PUT', `/api/app/opportunities/${id(opportunityId)}`, edit),
  setLines: (opportunityId: Guid, lines: OpportunityLineEdit[]) =>
    send<SetLinesResult>('PUT', `/api/app/opportunities/${id(opportunityId)}/lines`, { lines }),
  requestQuote: (opportunityId: Guid, userId: Guid) =>
    send<QuoteRequestResult>('POST', `/api/app/opportunities/${id(opportunityId)}/request-quote`, { userId }),
};
