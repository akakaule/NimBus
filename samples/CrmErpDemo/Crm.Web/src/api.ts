export interface Account {
  id: string;
  legalName: string;
  taxId?: string | null;
  countryCode: string;
  erpCustomerId?: string | null;
  erpCustomerNumber?: string | null;
  createdAt: string;
  updatedAt?: string | null;
  origin?: string;
  isDeleted?: boolean;
}

export interface AuditEntry {
  id: string;
  entityType: string;
  entityId: string;
  action: 'Created' | 'Updated' | 'Deleted' | string;
  fieldName?: string | null;
  oldValue?: string | null;
  newValue?: string | null;
  timestamp: string;
  origin?: string | null;
}

export interface Contact {
  id: string;
  accountId?: string | null;
  firstName: string;
  lastName: string;
  email?: string | null;
  phone?: string | null;
  createdAt: string;
  updatedAt?: string | null;
  origin?: string;
  isDeleted?: boolean;
}

async function json<T>(res: Response): Promise<T> {
  if (!res.ok) {
    const body = await res.text().catch(() => '');
    throw new Error(`${res.status} ${res.statusText}${body ? ` — ${body.slice(0, 500)}` : ''}`);
  }
  return res.json() as Promise<T>;
}

export const api = {
  listAccounts: () => fetch('/api/accounts').then(json<Account[]>),
  getAccount: (id: string) => fetch(`/api/accounts/${id}`).then(json<Account>),
  createAccount: (a: Partial<Account>) =>
    fetch('/api/accounts', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(a),
    }).then(json<Account>),
  updateAccount: (id: string, a: Partial<Account>) =>
    fetch(`/api/accounts/${id}`, {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(a),
    }).then(json<Account>),

  deleteAccount: (id: string) =>
    fetch(`/api/accounts/${id}`, { method: 'DELETE' }).then(json<Account>),

  listContacts: () => fetch('/api/contacts').then(json<Contact[]>),
  getContact: (id: string) => fetch(`/api/contacts/${id}`).then(json<Contact>),
  createContact: (c: Partial<Contact>) =>
    fetch('/api/contacts', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(c),
    }).then(json<Contact>),
  updateContact: (id: string, c: Partial<Contact>) =>
    fetch(`/api/contacts/${id}`, {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(c),
    }).then(json<Contact>),
  deleteContact: (id: string) =>
    fetch(`/api/contacts/${id}`, { method: 'DELETE' }).then(json<Contact>),

  getAuditLog: (entityType: 'Account' | 'Contact', entityId: string) =>
    fetch(`/api/audit/${entityType}/${entityId}`).then(json<AuditEntry[]>),
};
