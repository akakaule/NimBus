// Typed client for the Business Central simulator (/api/app/* for the BC screens, /api/demo/* for
// the hidden presenter pages) and the one D365 simulator call the cockpit makes (/d365-api/...).
// Shapes follow the simulator's camelCase JSON; decimals arrive as numbers, dates as ISO strings.

export type QuoteStatus = 'Draft' | 'Sent' | 'Accepted' | 'Expired';
export type SellToType = 'Contact' | 'Customer';
export type Blocked = '' | 'Ship' | 'Invoice' | 'All';

export interface AppInfo {
  companyName: string;
  companyId: string;
  environment: string;
}

export interface RecentDocument {
  type: 'Sales Quote' | 'Sales Order' | string;
  id: string;
  number: string;
  name: string;
  status: string;
  amount: number;
  currencyCode: string;
  lastModified: string;
}

export interface RoleCenter {
  openQuotes: number;
  sentQuotes: number;
  openQuotesValue: number;
  ordersThisMonth: number;
  ordersValueThisMonth: number;
  customers: number;
  customersBlocked: number;
  prospects: number;
  recentDocuments: RecentDocument[];
}

export interface QuoteSummary {
  id: string;
  number: string;
  sellToName: string;
  sellToType: SellToType;
  sellToNumber: string | null;
  externalDocumentNumber: string | null;
  description: string | null;
  salespersonCode: string | null;
  status: QuoteStatus | string;
  totalAmountExcludingTax: number;
  currencyCode: string;
  documentDate: string;
  validUntilDate: string | null;
  orderNumber: string | null;
  lineCount: number;
  lastModified: string;
}

export interface QuoteLine {
  id: string;
  quoteId: string;
  sequence: number;
  itemNumber: string;
  description: string;
  quantity: number;
  unitPrice: number;
  discountPercent: number;
  amountExcludingTax: number;
}

/** A prospect BC quotes before it buys; converted to a customer by Make Order. */
export interface Contact {
  id: string;
  number: string;
  type: string;
  displayName: string;
  addressLine1: string | null;
  city: string | null;
  postalCode: string | null;
  countryCode: string;
  phoneNumber: string | null;
  website: string | null;
  vatRegistrationNumber: string | null;
  contactPersonName: string | null;
  contactPersonEmail: string | null;
  contactPersonPhone: string | null;
  customerTemplateCode: string;
  crmAccountId: string | null;
  customerId: string | null;
  customerNumber: string | null;
  lastModifiedDateTime: string;
}

export interface Customer {
  id: string;
  number: string;
  displayName: string;
  type: string;
  addressLine1: string | null;
  city: string | null;
  postalCode: string | null;
  countryCode: string;
  phoneNumber: string | null;
  email: string | null;
  website: string | null;
  taxRegistrationNumber: string | null;
  salespersonCode: string | null;
  creditLimit: number;
  balanceDue: number;
  overdueAmount: number;
  blocked: Blocked | string;
  paymentTermsCode: string | null;
  currencyCode: string;
  crmAccountId: string | null;
  /** 0 = created in Business Central, 1 = converted from a CRM prospect. */
  origin: number;
  lastModifiedDateTime: string;
}

export interface CustomerTemplate {
  code: string;
  description: string;
  creditLimit: number;
  paymentTermsCode: string;
}

export interface QuoteDetail {
  id: string;
  number: string;
  description: string | null;
  externalDocumentNumber: string | null;
  documentDate: string;
  validUntilDate: string | null;
  status: QuoteStatus | string;
  sentDate: string | null;
  acceptedDate: string | null;
  salespersonCode: string | null;
  currencyCode: string;
  totalAmountExcludingTax: number;
  orderNumber: string | null;
  sellToName: string;
  crmOpportunityId: string | null;
  crmAccountId: string | null;
  lastModifiedDateTime: string;
  sellToType: SellToType;
  contact: Contact | null;
  customer: Customer | null;
  lines: QuoteLine[];
  proposedTemplate: CustomerTemplate | null;
}

export interface QuoteLineEdit {
  itemNumber: string;
  quantity: number;
  unitPrice: number;
  discountPercent: number;
}

export interface MakeOrderResult {
  orderId: string;
  orderNumber: string;
  customerId: string;
  customerNumber: string;
  customerCreated: boolean;
}

export interface SalesOrderLine {
  id: string;
  orderId: string;
  sequence: number;
  itemNumber: string;
  description: string;
  quantity: number;
  unitPrice: number;
  discountPercent: number;
  amountExcludingTax: number;
}

export interface SalesOrder {
  id: string;
  number: string;
  externalDocumentNumber: string | null;
  quoteNumber: string | null;
  orderDate: string;
  customerId: string;
  customerNumber: string;
  customerName: string;
  salespersonCode: string | null;
  status: string;
  currencyCode: string;
  totalAmountExcludingTax: number;
  crmOpportunityId: string | null;
  crmAccountId: string | null;
  lastModifiedDateTime: string;
  lines: SalesOrderLine[];
}

export interface CustomerDetail {
  customer: Customer;
  quotes: QuoteSummary[];
  orders: SalesOrder[];
}

/** The full body PUT /api/app/customers/{id} expects. */
export interface CustomerEdit {
  displayName: string;
  addressLine1: string | null;
  city: string | null;
  postalCode: string | null;
  countryCode: string;
  phoneNumber: string | null;
  email: string | null;
  website: string | null;
  salespersonCode: string | null;
  creditLimit: number;
  blocked: Blocked;
  paymentTermsCode: string | null;
}

export interface Salesperson {
  code: string;
  displayName: string;
  email: string;
  createdAt: string;
}

export interface NewSalesperson {
  code: string;
  displayName: string;
  email: string;
}

export interface Item {
  id: string;
  number: string;
  displayName: string;
  unitPrice: number;
  baseUnitOfMeasure: string;
  blocked: boolean;
}

// ---- Demo (hidden presenter pages) -----------------------------------------------------------

export interface FaultWindow {
  active: boolean;
  endsAt: string | null;
  remainingSeconds: number;
}

export interface FaultSnapshot {
  maintenance: FaultWindow;
  throttling: FaultWindow;
}

export type CircuitStateName = 'Closed' | 'Open' | 'HalfOpen';

export interface CircuitTransition {
  endpoint: string;
  from: CircuitStateName | string;
  to: CircuitStateName | string;
  reason: string | null;
  timestamp: string;
}

export interface CircuitState {
  state: CircuitStateName | string;
  reason: string | null;
  changedAt: string;
  history: CircuitTransition[];
}

export interface DemoState {
  faults: FaultSnapshot;
  circuit: CircuitState;
  alertCount: number;
}

export type AlertSeverity = 'Critical' | 'Error' | 'Warning' | 'Information';

export interface Alert {
  severity: AlertSeverity | string;
  title: string;
  message: string;
  eventId: string;
  eventTypeId: string;
  messageId: string;
  correlationId: string;
  errorDetails: string;
  receivedAt: string;
}

export interface BurstProspect {
  accountId: string;
  opportunityId: string;
  name: string;
  seller: string;
}

export interface BurstResult {
  count: number;
  created: BurstProspect[];
}

// ---- Transport -------------------------------------------------------------------------------

/** A failed call, carrying the BC error code when the simulator sent one. */
export class ApiError extends Error {
  readonly status: number;
  readonly code: string | undefined;

  constructor(message: string, status: number, code?: string) {
    super(message);
    this.name = 'ApiError';
    this.status = status;
    this.code = code;
  }
}

const BC = 'the Business Central (simulated) API';
const D365 = 'the Dynamics 365 Sales (simulated) API';

interface ErrorBody {
  error?: { code?: string; message?: string } | string;
  title?: string;
  detail?: string;
}

function parseBody(text: string): unknown {
  try {
    return JSON.parse(text) as unknown;
  } catch {
    return undefined;
  }
}

/**
 * Reads a response as JSON. BC-style errors ({"error":{"code","message"}}) and plain
 * {"error":"…"} bodies become an ApiError with that message; anything else falls back to the
 * status line.
 */
export async function json<T>(res: Response, service = BC): Promise<T> {
  const text = await res.text();
  if (res.ok) {
    return (text ? JSON.parse(text) : undefined) as T;
  }

  const body = (text ? parseBody(text) : undefined) as ErrorBody | undefined;
  const error = body?.error;
  if (error && typeof error === 'object' && error.message) {
    throw new ApiError(error.message, res.status, error.code);
  }
  if (typeof error === 'string' && error) {
    throw new ApiError(error, res.status);
  }
  if (body?.detail || body?.title) {
    throw new ApiError(body.detail || body.title || '', res.status);
  }
  if (res.status === 404) {
    throw new ApiError('The record does not exist.', res.status);
  }
  if (res.status >= 500) {
    throw new ApiError(`${capitalize(service)} is unavailable (${res.status} ${res.statusText}).`, res.status);
  }
  throw new ApiError(`${capitalize(service)} refused the request (${res.status} ${res.statusText}).`, res.status);
}

function capitalize(text: string): string {
  return text.charAt(0).toUpperCase() + text.slice(1);
}

interface RequestOptions {
  method?: 'GET' | 'POST' | 'PUT' | 'DELETE';
  body?: unknown;
  signal?: AbortSignal;
  service?: string;
}

async function request<T>(url: string, { method = 'GET', body, signal, service = BC }: RequestOptions = {}): Promise<T> {
  const headers: Record<string, string> = { Accept: 'application/json' };
  if (body !== undefined) headers['Content-Type'] = 'application/json';

  let res: Response;
  try {
    res = await fetch(url, {
      method,
      headers,
      body: body === undefined ? undefined : JSON.stringify(body),
      signal,
    });
  } catch (error) {
    if (signal?.aborted) throw error;
    throw new ApiError(`Can't reach ${service}. Check that the demo AppHost is running.`, 0);
  }
  return json<T>(res, service);
}

const get = <T>(url: string, signal?: AbortSignal) => request<T>(url, { signal });

// ---- Endpoints -------------------------------------------------------------------------------

export const api = {
  info: (signal?: AbortSignal) => get<AppInfo>('/api/app/info', signal),
  roleCenter: (signal?: AbortSignal) => get<RoleCenter>('/api/app/rolecenter', signal),

  quotes: (signal?: AbortSignal) => get<QuoteSummary[]>('/api/app/quotes', signal),
  quote: (id: string, signal?: AbortSignal) => get<QuoteDetail>(`/api/app/quotes/${id}`, signal),
  updateQuoteLines: (id: string, lines: QuoteLineEdit[]) =>
    request<QuoteDetail>(`/api/app/quotes/${id}/lines`, { method: 'PUT', body: { lines } }),
  sendQuote: (id: string) => request<QuoteDetail>(`/api/app/quotes/${id}/send`, { method: 'POST' }),
  makeOrder: (id: string, customerTemplateCode: string | null) =>
    request<MakeOrderResult>(`/api/app/quotes/${id}/make-order`, { method: 'POST', body: { customerTemplateCode } }),

  customers: (signal?: AbortSignal) => get<Customer[]>('/api/app/customers', signal),
  customer: (id: string, signal?: AbortSignal) => get<CustomerDetail>(`/api/app/customers/${id}`, signal),
  updateCustomer: (id: string, edit: CustomerEdit) =>
    request<Customer>(`/api/app/customers/${id}`, { method: 'PUT', body: edit }),

  contacts: (signal?: AbortSignal) => get<Contact[]>('/api/app/contacts', signal),

  salespeople: (signal?: AbortSignal) => get<Salesperson[]>('/api/app/salespeople', signal),
  createSalesperson: (salesperson: NewSalesperson) =>
    request<Salesperson>('/api/app/salespeople', { method: 'POST', body: salesperson }),

  orders: (signal?: AbortSignal) => get<SalesOrder[]>('/api/app/orders', signal),
  order: (id: string, signal?: AbortSignal) => get<SalesOrder>(`/api/app/orders/${id}`, signal),

  items: (signal?: AbortSignal) => get<Item[]>('/api/app/items', signal),
  templates: (signal?: AbortSignal) => get<CustomerTemplate[]>('/api/app/templates', signal),
};

export const demoApi = {
  state: (signal?: AbortSignal) => get<DemoState>('/api/demo/state', signal),
  setMaintenance: (seconds: number) =>
    request<FaultSnapshot>('/api/demo/maintenance', { method: 'PUT', body: { seconds } }),
  setThrottling: (seconds: number) =>
    request<FaultSnapshot>('/api/demo/throttling', { method: 'PUT', body: { seconds } }),
  alerts: (signal?: AbortSignal) => get<Alert[]>('/api/demo/alerts', signal),
  clearAlerts: () => request<void>('/api/demo/alerts', { method: 'DELETE' }),
  burst: (count: number) =>
    request<BurstResult>('/d365-api/api/demo/burst', { method: 'POST', body: { count }, service: D365 }),
};

/** The message to show for any thrown value. */
export function errorMessage(error: unknown): string {
  if (error instanceof Error && error.message) return error.message;
  return 'Something went wrong.';
}
